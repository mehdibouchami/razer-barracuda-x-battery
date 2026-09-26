using System.Reflection;

[assembly: AssemblyTitle("Barracuda Battery")]
[assembly: AssemblyDescription("Battery level tray indicator for the Razer Barracuda X (2022) 2.4 GHz dongle")]
[assembly: AssemblyProduct("Barracuda Battery")]
[assembly: AssemblyCompany("Mehdi Bouchami")]
[assembly: AssemblyCopyright("Copyright (c) 2026 Mehdi Bouchami. MIT License.")]
[assembly: AssemblyVersion(BarracudaBattery.AppInfo.Version + ".0")]
[assembly: AssemblyFileVersion(BarracudaBattery.AppInfo.Version + ".0")]

namespace BarracudaBattery
{
    static class AppInfo
    {
        public const string Version = "1.0.0";
        public const string Author = "Mehdi Bouchami";
        public const string GitHubUrl = "https://github.com/mehdibouchami/razer-barracuda-x-battery";
        // Donation page. Empty = the "Support" menu item is hidden.
        public const string SponsorUrl = "https://ba9chich.com/fr/mehdibouchami";
    }
}
