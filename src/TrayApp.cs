using System;
using System.Drawing;
using System.Drawing.Text;
using System.Runtime.InteropServices;
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
        const int PollIntervalMs = 60 * 1000;
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
            menu.Items.Add("Refresh now", null, delegate { Refresh(); });
            menu.Items.Add(startupItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("GitHub", null, delegate { OpenUrl(AppInfo.GitHubUrl); });
            if (AppInfo.SponsorUrl.Length > 0)
                menu.Items.Add("Support this project ♥", null, delegate { OpenUrl(AppInfo.SponsorUrl); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate { ExitThread(); });

            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { Refresh(); };
            tray.Visible = true;
            Show(null, "Barracuda X: checking...");

            timer.Interval = PollIntervalMs;
            timer.Tick += delegate { Refresh(); };
            timer.Start();
            Refresh();
        }

        void Refresh()
        {
            // HID I/O blocks for up to a few seconds; keep it off the UI thread and never overlap queries.
            if (Interlocked.Exchange(ref busy, 1) == 1) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                BatteryReading reading = null;
                string status;
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
                ui.Post(delegate
                {
                    Interlocked.Exchange(ref busy, 0);
                    Show(reading, status);
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
            Size size = SystemInformation.SmallIconSize;
            using (var bmp = new Bitmap(size.Width, size.Height))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);

                string label = r == null ? "--" : (r.Percent >= 100 ? "F" : r.Percent.ToString());
                Color color = r == null ? Color.Gray
                    : r.Percent <= LowBatteryPercent ? Color.FromArgb(255, 70, 70)
                    : r.Percent <= 50 ? Color.FromArgb(255, 200, 40)
                    : Color.FromArgb(68, 214, 44); // Razer green

                // Pick the largest font that fits the icon
                float em = size.Height;
                Font font = null;
                SizeF measured;
                do
                {
                    if (font != null) font.Dispose();
                    font = new Font("Segoe UI", em, FontStyle.Bold, GraphicsUnit.Pixel);
                    measured = g.MeasureString(label, font, PointF.Empty, StringFormat.GenericTypographic);
                    em -= 0.5f;
                } while ((measured.Width > size.Width || measured.Height > size.Height) && em > 4);

                using (font)
                using (var brush = new SolidBrush(color))
                {
                    float x = (size.Width - measured.Width) / 2;
                    float y = (size.Height - measured.Height) / 2;
                    g.DrawString(label, font, brush, x, y, StringFormat.GenericTypographic);
                }

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
