using System;
using System.IO;

namespace BarracudaBattery
{
    /// <summary>Small rolling log in %LOCALAPPDATA%\BarracudaBattery (log.txt, previous one kept as log.old.txt).</summary>
    static class Log
    {
        const long MaxBytes = 512 * 1024;
        static readonly object gate = new object();

        public static readonly string Folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BarracudaBattery");

        static string FilePath
        {
            get { return Path.Combine(Folder, "log.txt"); }
        }

        static string LastPercentPath
        {
            get { return Path.Combine(Folder, "last-level.txt"); }
        }

        /// <summary>Last level shown, so a restart while the charger is plugged in still has one. -1 if unknown.</summary>
        public static int ReadLastPercent()
        {
            try
            {
                int percent;
                if (File.Exists(LastPercentPath) && int.TryParse(File.ReadAllText(LastPercentPath).Trim(), out percent)
                    && percent >= 0 && percent <= 100)
                    return percent;
            }
            catch (Exception)
            {
            }
            return -1;
        }

        public static void WriteLastPercent(int percent)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllText(LastPercentPath, percent.ToString());
            }
            catch (Exception)
            {
            }
        }

        public static void Write(string text)
        {
            lock (gate)
            {
                try
                {
                    Directory.CreateDirectory(Folder);
                    var file = new FileInfo(FilePath);
                    if (file.Exists && file.Length > MaxBytes)
                    {
                        string old = Path.Combine(Folder, "log.old.txt");
                        File.Delete(old);
                        File.Move(FilePath, old);
                    }
                    File.AppendAllText(FilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + text
                        + Environment.NewLine);
                }
                catch (Exception)
                {
                    // Logging must never break the app
                }
            }
        }
    }
}
