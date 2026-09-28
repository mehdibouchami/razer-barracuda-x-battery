using System;
using System.Linq;

namespace BarracudaBattery
{
    static class Probe
    {
        static int Main()
        {
            var all = Hid.Enumerate(BarracudaProtocol.RazerVid, BarracudaProtocol.DonglePids);
            Console.WriteLine("Found {0} matching HID interface(s):", all.Count);
            foreach (var i in all) Console.WriteLine("  " + i);

            BarracudaProtocol.Trace = (dir, data) =>
                Console.WriteLine("{0} {1}", dir, string.Join(" ", data.Take(24).Select(b => b.ToString("X2"))));

            string status;
            var r = BarracudaProtocol.Read(out status);
            if (r == null)
            {
                Console.WriteLine("\nNo battery reading: " + status);
                return 2;
            }
            Console.WriteLine("\nBattery: {0}% ({1} mV){2}", r.Percent, r.Millivolts,
                r.Charging ? "  CHARGING (level unreliable while plugged in)" : "");
            return 0;
        }
    }
}
