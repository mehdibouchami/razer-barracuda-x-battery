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

            HidInfo ctrl = BarracudaProtocol.FindControlInterface();
            if (ctrl == null)
            {
                Console.WriteLine("No vendor (0xFF00) interface found. Is the dongle plugged in?");
                return 1;
            }
            Console.WriteLine("\nUsing: " + ctrl.Path);

            BarracudaProtocol.Trace = (dir, data) =>
                Console.WriteLine("{0} {1}", dir, string.Join(" ", data.Take(24).Select(b => b.ToString("X2"))));

            using (var dev = new HidDevice(ctrl))
            {
                var r = BarracudaProtocol.Query(dev);
                if (r == null)
                {
                    Console.WriteLine("\nNo battery reading: headset off, on Bluetooth, or out of range.");
                    return 2;
                }
                Console.WriteLine("\nBattery: {0}% ({1} mV)", r.Percent, r.Millivolts);
            }
            return 0;
        }
    }
}
