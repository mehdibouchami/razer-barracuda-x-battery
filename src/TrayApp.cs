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
        int shownPercent = -1;
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
            menu.Items.Add("Open log folder", null, delegate { OpenUrl(Log.Folder); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("GitHub", null, delegate { OpenUrl(AppInfo.GitHubUrl); });
            if (AppInfo.SponsorUrl.Length > 0)
                menu.Items.Add("Support this project ♥", null, delegate { OpenUrl(AppInfo.SponsorUrl); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate { ExitThread(); });

            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { Refresh("manual"); };
            tray.Visible = true;
            Show(null, "checking...");

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
                    HidInfo info = BarracudaProtocol.FindControlInterface();
                    if (info == null)
                    {
                        status = "dongle not found";
                    }
                    else
                    {
                        using (var dev = new HidDevice(info))
                            reading = BarracudaProtocol.Query(dev);
                        status = reading == null ? "headset off or out of range" : null;
                    }
                }
                catch (Exception e)
                {
                    status = "error: " + e.Message;
                }
                BarracudaProtocol.Trace = null;

                if (reading != null)
                    Log.Write(string.Format("{0}: {1}% ({2} mV)", trigger, reading.Percent, reading.Millivolts));
                else
                    Log.Write(trigger + ": " + status + Environment.NewLine + trace.ToString().TrimEnd());

                ui.Post(delegate
                {
                    Interlocked.Exchange(ref busy, 0);
                    Show(reading, status);
                    // The dongle stays plugged in when the headset turns off or switches to Bluetooth, so there's
                    // no device event for its return: poll on every tick while it's away to pick it up quickly.
                    // (The 2 s slack keeps tick jitter from pushing a due poll to the following tick.)
                    nextPollUtc = reading == null ? DateTime.UtcNow
                        : DateTime.UtcNow.AddMilliseconds(ConnectedPollMs - 2000);
                }, null);
            });
        }

        void Show(BatteryReading r, string status)
        {
            string text;
            if (r != null)
            {
                r = new BatteryReading { Millivolts = r.Millivolts, Percent = Step(r.Percent) };
                text = string.Format("Barracuda X: {0}% ({1:0.00} V)", r.Percent, r.Millivolts / 1000.0);
                if (r.Percent <= LowBatteryPercent && !lowWarned)
                {
                    lowWarned = true;
                    tray.ShowBalloonTip(5000, "Barracuda X battery low", r.Percent + "% remaining - time to charge.",
                        ToolTipIcon.Warning);
                }
                else if (r.Percent > LowBatteryPercent + 5)
                {
                    lowWarned = false;
                }
            }
            else
            {
                text = "Barracuda X: " + status;
            }
            tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;
            SetIcon(r);
        }

        /// <summary>
        /// Rounds to 10% steps like Razer's app. Hysteresis: the shown step only changes once the estimate is
        /// 7+ points away from it (2 past the rounding midpoint), so voltage noise doesn't make it flicker.
        /// </summary>
        int Step(int percent)
        {
            if (shownPercent < 0 || Math.Abs(percent - shownPercent) >= 7)
                shownPercent = (percent + 5) / 10 * 10;
            return shownPercent;
        }

        void SetIcon(BatteryReading r)
        {
            // "100%" doesn't fit a 16 px icon; a full battery shows "100" alone
            string label = r == null ? "--" : r.Percent >= 100 ? "100" : r.Percent + "%";
            Color color = r == null ? Color.Gray
                : r.Percent <= LowBatteryPercent ? Color.FromArgb(255, 70, 70)
                : r.Percent <= 50 ? Color.FromArgb(255, 200, 40)
                : Color.FromArgb(68, 214, 44); // Razer green

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
