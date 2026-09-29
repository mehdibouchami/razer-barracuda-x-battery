using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace BarracudaBattery
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool first;
            using (new Mutex(true, "BarracudaBatteryTray", out first))
            {
                if (!first) return;
                Application.EnableVisualStyles();
                Application.Run(new TrayApp());
            }
        }
    }

    sealed class TrayApp : ApplicationContext
    {
        // The timer ticks at a fixed rate and each tick decides whether a query is due. (Changing a running
        // WinForms timer's Interval stopped it from ticking, so the interval never changes.)
        const int TickMs = 10 * 1000;             // poll rate while the headset is off / on Bluetooth
        const int ConnectedPollMs = 30 * 1000;    // poll rate while connected
        const int LowBatteryPercent = 20;
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValue = "BarracudaBattery";

        readonly NotifyIcon tray = new NotifyIcon();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        readonly ToolStripMenuItem startupItem = new ToolStripMenuItem("Start with Windows");
        readonly SynchronizationContext ui;
        int busy;
        bool lowWarned;
        int shownPercent = Log.ReadLastPercent(); // survives restarts, so a restart while charging still has a level
        string lastLoggedState;
        bool chargingSticky;
        int notChargingStreak;
        const int NotChargingChecks = 3; // ~1.5 min of "not charging" before the level starts moving again
        DateTime nextPollUtc = DateTime.MaxValue; // set after the startup query completes
        IntPtr iconHandle;

        public TrayApp()
        {
            ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

            startupItem.Checked = IsStartupEnabled();
            startupItem.Click += delegate { SetStartup(!startupItem.Checked); startupItem.Checked = IsStartupEnabled(); };

            var title = new ToolStripMenuItem("Barracuda Battery v" + AppInfo.Version + " by " + AppInfo.Author);
            title.Enabled = false;

            var menu = new ContextMenuStrip();
            menu.Items.Add(title);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Refresh now", null, delegate { Refresh("manual"); });
            menu.Items.Add(startupItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("GitHub", null, delegate { OpenUrl(AppInfo.GitHubUrl); });
            if (AppInfo.SponsorUrl.Length > 0)
                menu.Items.Add("Support this project ♥", null, delegate { OpenUrl(AppInfo.SponsorUrl); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate { ExitThread(); });

            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { Refresh("manual"); };
            tray.Visible = true;
            Show(null, "checking...", false);

            Log.Write("started v" + AppInfo.Version);
            timer.Interval = TickMs;
            timer.Tick += delegate { if (DateTime.UtcNow >= nextPollUtc) Refresh("timer"); };
            timer.Start();
            Refresh("startup");
        }

        void Refresh(string trigger)
        {
            // HID I/O blocks for up to a few seconds; keep it off the UI thread and never overlap queries.
            if (Interlocked.Exchange(ref busy, 1) == 1)
            {
                Log.Write(trigger + ": skipped, previous query still running");
                return;
            }
            ThreadPool.QueueUserWorkItem(delegate
            {
                BatteryReading reading = null;
                string status;
                var trace = new StringBuilder();
                BarracudaProtocol.Trace = (dir, data) => trace.AppendLine("      " + dir + " "
                    + BitConverter.ToString(data, 0, Math.Min(data.Length, 24)).Replace('-', ' '));
                try
                {
                    reading = BarracudaProtocol.Read(out status);
                }
                catch (Exception e)
                {
                    status = "error: " + e.Message;
                }
                BarracudaProtocol.Trace = null;

                ui.Post(delegate
                {
                    Interlocked.Exchange(ref busy, 0);
                    bool charging = reading != null && IsCharging(reading);

                    // Log only changes (connected / charging / headset away / dongle missing...), with the raw
                    // exchange for failures, so the log stays small while still showing what happened.
                    string state = reading != null ? (charging ? "charging" : "connected") : status;
                    if (state != lastLoggedState)
                    {
                        lastLoggedState = state;
                        if (reading != null)
                            Log.Write(string.Format("{0}: connected, {1}% ({2} mV){3}", trigger, reading.Percent,
                                reading.Millivolts, charging ? ", charging" : ""));
                        else
                            Log.Write(trigger + ": " + status + Environment.NewLine + trace.ToString().TrimEnd());
                    }

                    Show(reading, status, charging);
                    // The dongle stays plugged in when the headset turns off or switches to Bluetooth, so there's
                    // no device event for its return: poll on every tick while it's away to pick it up quickly.
                    // (The 2 s slack keeps tick jitter from pushing a due poll to the following tick.)
                    nextPollUtc = reading == null ? DateTime.UtcNow
                        : DateTime.UtcNow.AddMilliseconds(ConnectedPollMs - 2000);
                }, null);
            });
        }

        /// <summary>
        /// Charging detection can miss a check (the voltage swing isn't always visible in one sample window), and
        /// a single miss would let the charger-inflated voltage update the level - which is how it used to creep
        /// up to 100% mid-charge. So charging sticks until several checks in a row say otherwise.
        /// </summary>
        bool IsCharging(BatteryReading r)
        {
            if (r.Charging)
            {
                chargingSticky = true;
                notChargingStreak = 0;
                return true;
            }
            if (!chargingSticky) return false;
            if (++notChargingStreak < NotChargingChecks) return true;
            chargingSticky = false;
            return false;
        }

        void Show(BatteryReading r, string status, bool charging)
        {
            string text, label;
            Color color;

            if (r == null)
            {
                text = "Barracuda X: " + status;
                label = "--";
                color = Color.Gray;
            }
            else if (charging)
            {
                // The charger raises the voltage, so the level can't be estimated while plugged in: keep showing
                // the last level from before charging (blue), rather than a wrong percentage.
                string volts = string.Format("{0:0.00} V", r.Millivolts / 1000.0);
                text = shownPercent >= 0
                    ? string.Format("Barracuda X: charging ({0} V, was {1}%)", volts, shownPercent)
                    : "Barracuda X: charging (" + volts + ")";
                label = shownPercent >= 0 ? shownPercent.ToString() : "--";
                color = Color.FromArgb(80, 170, 255);
                lowWarned = false;
            }
            else
            {
                int percent = Step(r.Percent);
                text = string.Format("Barracuda X: {0}% ({1:0.00} V)", percent, r.Millivolts / 1000.0);
                label = percent.ToString();
                color = percent <= LowBatteryPercent ? Color.FromArgb(255, 70, 70)
                    : percent <= 50 ? Color.FromArgb(255, 200, 40)
                    : Color.FromArgb(68, 214, 44); // Razer green

                if (percent <= LowBatteryPercent && !lowWarned)
                {
                    lowWarned = true;
                    tray.ShowBalloonTip(5000, "Barracuda X battery low", percent + "% remaining - time to charge.",
                        ToolTipIcon.Warning);
                }
                else if (percent > LowBatteryPercent + 5)
                {
                    lowWarned = false;
                }
            }

            tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;
            SetIcon(label, color);
        }

        /// <summary>
        /// Rounds to 5% steps. Hysteresis: the shown step only changes once the estimate is 4+ points away from
        /// it (1.5 past the rounding midpoint), so voltage noise doesn't make it flicker.
        /// </summary>
        int Step(int percent)
        {
            if (shownPercent < 0 || Math.Abs(percent - shownPercent) >= 4)
            {
                shownPercent = (percent + 2) / 5 * 5;
                Log.WriteLastPercent(shownPercent);
            }
            return shownPercent;
        }

        void SetIcon(string label, Color color)
        {
            using (Bitmap bmp = IconRenderer.Render(SystemInformation.SmallIconSize, label, color))
            {
                IntPtr old = iconHandle;
                iconHandle = bmp.GetHicon();
                tray.Icon = Icon.FromHandle(iconHandle);
                if (old != IntPtr.Zero) DestroyIcon(old);
            }
        }

        static void OpenUrl(string url)
        {
            try { System.Diagnostics.Process.Start(url); }
            catch (Exception) { }
        }

        static bool IsStartupEnabled()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey))
                return key != null && key.GetValue(RunValue) != null;
        }

        static void SetStartup(bool enable)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enable) key.SetValue(RunValue, "\"" + Application.ExecutablePath + "\"");
                else key.DeleteValue(RunValue, false);
            }
        }

        protected override void ExitThreadCore()
        {
            timer.Stop();
            tray.Visible = false;
            tray.Dispose();
            if (iconHandle != IntPtr.Zero) DestroyIcon(iconHandle);
            base.ExitThreadCore();
        }

        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr handle);
    }
}
