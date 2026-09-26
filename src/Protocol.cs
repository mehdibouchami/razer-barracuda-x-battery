using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace BarracudaBattery
{
    public class BatteryReading
    {
        public int Millivolts;
        public int Percent;
        public bool Charging;
    }

    /// <summary>
    /// Battery query for the Razer Barracuda X (2022) 2.4 GHz dongle.
    ///
    /// The headset is an Airoha chip; the dongle bridges USB HID to it. We send the Airoha "RACE" command
    /// GET_BATTERY (50 41 06 seq 01 00 31) and the reply carries the battery voltage in millivolts.
    /// The dongle must be switched to "remote mode" (50 41 0E seq 02 E1 01) first so it forwards the command to
    /// the headset; we switch back to local mode afterwards (E1 00), as Razer's tool does.
    /// Two dongle bridge chips exist, with different HID framing (identified by the vendor collection's report ID):
    ///   0x02 - YS-Tech bridge (PID 0x0550): [02 80 len crcLo crcHi payload...], CRC-16/XMODEM over the whole
    ///          64-byte report; the write is acknowledged via GetInputReport ('O' ok / 'F' busy, retried);
    ///          replies arrive as input reports [02 len payload...].
    ///   0x01 - Macronix bridge (PID 0x0552): [01 80 len payload...] after an "app mode" handshake; replies are
    ///          [01 80 len payload...]. Per github.com/KhromotozzDevOut/razer-barracuda-battery-tray (MIT).
    /// The framing matches Razer's firmware updater library (AWToolLIB2: HID.ystech_write64 / write_aw_command).
    /// </summary>
    public static class BarracudaProtocol
    {
        public const ushort RazerVid = 0x1532;
        public static readonly ushort[] DonglePids = { 0x0550, 0x0552 };
        const ushort VendorUsagePage = 0xFF00;
        const int DefaultTimeoutMs = 800;
        static int timeoutOverrideMs;
        static int TimeoutMs
        {
            get { return timeoutOverrideMs > 0 ? timeoutOverrideMs : DefaultTimeoutMs; }
        }
        const byte RaceGetBattery = 0x31;
        const int ChargeSamples = 5;      // spread over ~1.5 s: two consecutive samples can match while charging
        const int ChargeJitterMv = 15;   // spread across samples: ~3 mV on battery, 30-45 mV while charging
        const int ChargeVoltageMv = 4180; // above any resting voltage, so the charger must be connected

        // Battery voltage (mV) for 0%, 10%, ... 100%: typical Li-ion discharge curve,
        // checked against the Razer mobile app (3975 mV = 70%).
        static readonly int[] Curve = { 3300, 3680, 3750, 3790, 3830, 3870, 3910, 3960, 4020, 4080, 4150 };

        static byte sequence;

        /// <summary>Optional hook that receives every raw packet sent (">") and received ("<").</summary>
        public static Action<string, byte[]> Trace;

        public static HidInfo FindControlInterface()
        {
            return Hid.Enumerate(RazerVid, DonglePids)
                .FirstOrDefault(i => i.UsagePage == VendorUsagePage && i.OutputLength > 0 && i.InputLength > 0);
        }

        /// <summary>Returns null when the dongle is present but the headset doesn't answer (e.g. powered off).</summary>
        public static BatteryReading Query(HidDevice dev)
        {
            dev.Flush();
            if (dev.Info.OutputReportId != 0x02 && !EnterMacronixAppMode(dev)) return null;

            // Remote mode makes the dongle forward commands to the headset; in local mode the dongle
            // answers GET_BATTERY itself with a meaningless value.
            byte[] reply = Race(dev, 0x0E, 0x02, 0xE1, 0x01);
            if (reply == null || reply[12] != 0x00) return null;

            // The headset reports voltage only - no charging flag and no percentage (Bluetooth has a standard
            // battery level, 2.4 GHz doesn't). While charging, the voltage is raised by the charger and swings
            // by tens of millivolts between samples; on battery it is steady within a few millivolts. So take a
            // few samples and use their spread to tell the two apart.
            var samples = new List<int>();
            try
            {
                for (int i = 0; i < ChargeSamples; i++)
                {
                    if (i > 0) Thread.Sleep(350);
                    reply = Race(dev, 0x06, 0x01, 0x00, RaceGetBattery);
                    // Response: [12]=status, [13..14]=millivolts (LE)
                    if (reply == null || reply.Length < 15 || reply[12] != 0x00) continue;
                    int sample = reply[13] | (reply[14] << 8);
                    if (sample >= 2500 && sample <= 4500) samples.Add(sample);
                }
            }
            finally
            {
                Race(dev, 0x0E, 0x02, 0xE1, 0x00);
            }
            if (samples.Count == 0) return null;

            int mv = samples.Min();
            bool charging = samples.Max() - samples.Min() >= ChargeJitterMv || mv >= ChargeVoltageMv;
            return new BatteryReading { Millivolts = mv, Percent = ToPercent(mv), Charging = charging };
        }

        /// <summary>Diagnostics only (probe/tools): sends one RACE command with a custom timeout.</summary>
        public static byte[] Debug_Race(HidDevice dev, int timeoutMs, byte group, params byte[] body)
        {
            int saved = timeoutOverrideMs;
            timeoutOverrideMs = timeoutMs;
            try { return Race(dev, group, body); }
            finally { timeoutOverrideMs = saved; }
        }

        /// <summary>Sends an Airoha RACE command (50 41 group seq body...) and returns the matching
        /// response payload (50 49 ... [10]=group, [11]=0x80|seq, [12]=status ...), or null.</summary>
        static byte[] Race(HidDevice dev, byte group, params byte[] body)
        {
            byte seq = NextSequence();
            var race = new byte[4 + body.Length];
            race[0] = 0x50;
            race[1] = 0x41;
            race[2] = group;
            race[3] = seq;
            Array.Copy(body, 0, race, 4, body.Length);
            Func<byte[], bool> matches = p => p.Length >= 13 && p[0] == 0x50 && p[1] == 0x49
                && p[10] == group && p[11] == (0x80 | seq);
            return dev.Info.OutputReportId == 0x02 ? SendYsTech(dev, race, matches) : SendMacronix(dev, race, matches);
        }

        static byte NextSequence()
        {
            sequence = (byte)(sequence % 127 + 1); // 1..127, as Razer's tool does
            return sequence;
        }

        // ---- YS-Tech bridge (report ID 0x02) ----

        static byte[] SendYsTech(HidDevice dev, byte[] race, Func<byte[], bool> matches)
        {
            var frame = new byte[dev.Info.OutputLength];
            frame[0] = 0x02;
            frame[1] = 0x80;
            frame[2] = (byte)race.Length;
            Array.Copy(race, 0, frame, 5, race.Length);
            ushort crc = Crc16Xmodem(frame);
            frame[3] = (byte)crc;
            frame[4] = (byte)(crc >> 8);

            bool accepted = false;
            for (int attempt = 0; attempt < 5 && !accepted; attempt++)
            {
                if (attempt > 0) Thread.Sleep(50);
                if (Trace != null) Trace(">", frame.Take(5 + race.Length).ToArray());
                if (!dev.Write(frame, TimeoutMs)) return null;
                byte[] status = dev.GetInputReport(0x02);
                if (status == null) return null;
                if (Trace != null) Trace("?", status.Take(2).ToArray());
                if (status[1] == (byte)'O') accepted = true;
                else if (status[1] != (byte)'F') return null;
            }
            if (!accepted) return null;

            return ReadUntil(dev, r =>
            {
                if (r.Length < 2 || r[0] != 0x02 || r[1] > r.Length - 2) return null;
                var payload = new byte[r[1]];
                Array.Copy(r, 2, payload, 0, payload.Length);
                return matches(payload) ? payload : null;
            });
        }

        static ushort Crc16Xmodem(byte[] data)
        {
            ushort crc = 0;
            foreach (byte b in data)
            {
                crc ^= (ushort)(b << 8);
                for (int i = 0; i < 8; i++)
                    crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
            }
            return crc;
        }

        // ---- Macronix bridge (report ID 0x01) ----

        static bool EnterMacronixAppMode(HidDevice dev)
        {
            byte id = dev.Info.OutputReportId;
            if (Trace != null) Trace(">", new byte[] { id, 0x40 });
            if (!dev.Write(new byte[] { id, 0x40 }, TimeoutMs)) return false;
            byte[] mode = ReadUntil(dev, r => r.Length >= 4 && r[0] == id && r[1] == 0x40 ? r : null);
            return mode != null && mode[2] == 0x01 && mode[3] == 0x01;
        }

        static byte[] SendMacronix(HidDevice dev, byte[] race, Func<byte[], bool> matches)
        {
            byte id = dev.Info.OutputReportId;
            var frame = new byte[3 + race.Length];
            frame[0] = id;
            frame[1] = 0x80;
            frame[2] = (byte)race.Length;
            Array.Copy(race, 0, frame, 3, race.Length);
            if (Trace != null) Trace(">", frame);
            if (!dev.Write(frame, TimeoutMs)) return null;

            return ReadUntil(dev, r =>
            {
                if (r.Length < 3 || r[0] != id || r[1] != 0x80 || r[2] > r.Length - 3) return null;
                var payload = new byte[r[2]];
                Array.Copy(r, 3, payload, 0, payload.Length);
                return matches(payload) ? payload : null;
            });
        }

        /// <summary>Reads input reports until <paramref name="extract"/> returns non-null, or the timeout expires.
        /// The dongle interleaves unrelated reports, which are skipped.</summary>
        static byte[] ReadUntil(HidDevice dev, Func<byte[], byte[]> extract)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(TimeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                int remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                byte[] r = dev.Read(Math.Max(remaining, 1));
                if (r == null) return null;
                if (Trace != null) Trace("<", r);
                byte[] result = extract(r);
                if (result != null) return result;
            }
            return null;
        }

        /// <summary>Linear interpolation on the Li-ion discharge curve.</summary>
        public static int ToPercent(int mv)
        {
            if (mv <= Curve[0]) return 0;
            for (int i = 1; i < Curve.Length; i++)
            {
                if (mv < Curve[i])
                    return (i - 1) * 10 + (mv - Curve[i - 1]) * 10 / (Curve[i] - Curve[i - 1]);
            }
            return 100;
        }
    }
}
