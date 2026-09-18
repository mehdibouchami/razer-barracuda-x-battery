using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace BarracudaBattery
{
    public class HidInfo
    {
        public string Path;
        public ushort VendorId;
        public ushort ProductId;
        public ushort UsagePage;
        public ushort Usage;
        public int InputLength;
        public int OutputLength;
        public int FeatureLength;
        public byte OutputReportId;

        public override string ToString()
        {
            return string.Format("{0:X4}:{1:X4} page=0x{2:X4} usage=0x{3:X4} in={4} out={5} feat={6} outId=0x{7:X2}\n    {8}",
                VendorId, ProductId, UsagePage, Usage, InputLength, OutputLength, FeatureLength, OutputReportId, Path);
        }
    }

    public static class Hid
    {
        public static List<HidInfo> Enumerate(ushort vendorId, ushort[] productIds)
        {
            var result = new List<HidInfo>();
            Guid hidGuid;
            Native.HidD_GetHidGuid(out hidGuid);
            IntPtr set = Native.SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero,
                Native.DIGCF_PRESENT | Native.DIGCF_DEVICEINTERFACE);
            if (set == new IntPtr(-1)) return result;
            try
            {
                var ifData = new Native.SP_DEVICE_INTERFACE_DATA();
                ifData.cbSize = Marshal.SizeOf(ifData);
                for (uint i = 0; Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, i, ref ifData); i++)
                {
                    string path = GetPath(set, ref ifData);
                    if (path == null) continue;
                    HidInfo info = Query(path);
                    if (info == null || info.VendorId != vendorId) continue;
                    if (Array.IndexOf(productIds, info.ProductId) < 0) continue;
                    result.Add(info);
                }
            }
            finally
            {
                Native.SetupDiDestroyDeviceInfoList(set);
            }
            return result;
        }

        static string GetPath(IntPtr set, ref Native.SP_DEVICE_INTERFACE_DATA ifData)
        {
            int size;
            Native.SetupDiGetDeviceInterfaceDetail(set, ref ifData, IntPtr.Zero, 0, out size, IntPtr.Zero);
            if (size <= 0) return null;
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W: 8 on x64, 6 on x86
                Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                if (!Native.SetupDiGetDeviceInterfaceDetail(set, ref ifData, buf, size, out size, IntPtr.Zero))
                    return null;
                return Marshal.PtrToStringUni(new IntPtr(buf.ToInt64() + 4));
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }

        static byte GetOutputReportId(IntPtr preparsed)
        {
            // HIDP_VALUE_CAPS is 72 bytes; ReportID is the byte at offset 2
            const int ValueCapsSize = 72;
            ushort count = 16;
            IntPtr buf = Marshal.AllocHGlobal(ValueCapsSize * count);
            try
            {
                if (Native.HidP_GetValueCaps(Native.HidP_Output, buf, ref count, preparsed) != Native.HIDP_STATUS_SUCCESS
                    || count == 0)
                    return 0;
                return Marshal.ReadByte(buf, 2);
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }

        static HidInfo Query(string path)
        {
            // Zero access rights: enough to read attributes even for devices opened exclusively
            using (SafeFileHandle h = Native.CreateFile(path, 0, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
                IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero))
            {
                if (h.IsInvalid) return null;
                var attrs = new Native.HIDD_ATTRIBUTES();
                attrs.Size = Marshal.SizeOf(attrs);
                if (!Native.HidD_GetAttributes(h, ref attrs)) return null;
                var info = new HidInfo { Path = path, VendorId = attrs.VendorID, ProductId = attrs.ProductID };
                IntPtr pre;
                if (Native.HidD_GetPreparsedData(h, out pre))
                {
                    var caps = new Native.HIDP_CAPS();
                    if (Native.HidP_GetCaps(pre, ref caps) == Native.HIDP_STATUS_SUCCESS)
                    {
                        info.UsagePage = caps.UsagePage;
                        info.Usage = caps.Usage;
                        info.InputLength = caps.InputReportByteLength;
                        info.OutputLength = caps.OutputReportByteLength;
                        info.FeatureLength = caps.FeatureReportByteLength;
                        info.OutputReportId = GetOutputReportId(pre);
                    }
                    Native.HidD_FreePreparsedData(pre);
                }
                return info;
            }
        }
    }

    /// <summary>Read/write handle to one HID interface using overlapped I/O with timeouts.</summary>
    public sealed class HidDevice : IDisposable
    {
        readonly SafeFileHandle handle;
        public readonly HidInfo Info;

        public HidDevice(HidInfo info)
        {
            Info = info;
            handle = Native.CreateFile(info.Path, Native.GENERIC_READ | Native.GENERIC_WRITE,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING,
                Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot open " + info.Path);
        }

        public void Flush()
        {
            Native.HidD_FlushQueue(handle);
        }

        /// <summary>Synchronously fetches an input report over the control pipe (HidD_GetInputReport).</summary>
        public byte[] GetInputReport(byte reportId)
        {
            var buf = new byte[Info.InputLength];
            buf[0] = reportId;
            return Native.HidD_GetInputReport(handle, buf, buf.Length) ? buf : null;
        }

        public bool Write(byte[] report, int timeoutMs)
        {
            var buf = new byte[Info.OutputLength];
            Array.Copy(report, buf, Math.Min(report.Length, buf.Length));
            return Io(buf, true, timeoutMs) == buf.Length;
        }

        /// <summary>Returns the input report, or null on timeout.</summary>
        public byte[] Read(int timeoutMs)
        {
            var buf = new byte[Info.InputLength];
            int n = Io(buf, false, timeoutMs);
            if (n <= 0) return null;
            if (n < buf.Length) Array.Resize(ref buf, n);
            return buf;
        }

        int Io(byte[] data, bool write, int timeoutMs)
        {
            IntPtr buf = Marshal.AllocHGlobal(data.Length);
            IntPtr ov = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(NativeOverlapped)));
            using (var evt = new ManualResetEvent(false))
            {
                try
                {
                    var o = new NativeOverlapped();
                    o.EventHandle = evt.SafeWaitHandle.DangerousGetHandle();
                    Marshal.StructureToPtr(o, ov, false);
                    if (write) Marshal.Copy(data, 0, buf, data.Length);

                    int dummy;
                    bool ok = write
                        ? Native.WriteFile(handle, buf, data.Length, out dummy, ov)
                        : Native.ReadFile(handle, buf, data.Length, out dummy, ov);
                    if (!ok)
                    {
                        int err = Marshal.GetLastWin32Error();
                        if (err != Native.ERROR_IO_PENDING) throw new Win32Exception(err);
                        if (!evt.WaitOne(timeoutMs))
                        {
                            Native.CancelIoEx(handle, ov);
                        }
                    }
                    int transferred;
                    // bWait=true: after a cancel this returns once the cancellation has completed,
                    // so the buffers are safe to free afterwards.
                    if (!Native.GetOverlappedResult(handle, ov, out transferred, true))
                    {
                        int err = Marshal.GetLastWin32Error();
                        if (err == Native.ERROR_OPERATION_ABORTED) return 0;
                        throw new Win32Exception(err);
                    }
                    if (!write) Marshal.Copy(buf, data, 0, transferred);
                    return transferred;
                }
                finally
                {
                    Marshal.FreeHGlobal(ov);
                    Marshal.FreeHGlobal(buf);
                }
            }
        }

        public void Dispose()
        {
            handle.Dispose();
        }
    }

    static class Native
    {
        public const int DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10;
        public const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
        public const uint FILE_FLAG_OVERLAPPED = 0x40000000;
        public const int ERROR_IO_PENDING = 997, ERROR_OPERATION_ABORTED = 995;
        public const int HIDP_STATUS_SUCCESS = 0x00110000;

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDD_ATTRIBUTES
        {
            public int Size;
            public ushort VendorID;
            public ushort ProductID;
            public ushort VersionNumber;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDP_CAPS
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
            public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps;
            public ushort NumberInputValueCaps;
            public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps;
            public ushort NumberOutputValueCaps;
            public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps;
            public ushort NumberFeatureValueCaps;
            public ushort NumberFeatureDataIndices;
        }

        [DllImport("hid.dll")]
        public static extern void HidD_GetHidGuid(out Guid guid);
        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES attrs);
        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);
        [DllImport("hid.dll")]
        public static extern bool HidD_FreePreparsedData(IntPtr data);
        [DllImport("hid.dll")]
        public static extern int HidP_GetCaps(IntPtr data, ref HIDP_CAPS caps);
        [DllImport("hid.dll")]
        public static extern bool HidD_FlushQueue(SafeFileHandle h);
        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_GetInputReport(SafeFileHandle h, byte[] buffer, int length);
        public const int HidP_Output = 1;
        [DllImport("hid.dll")]
        public static extern int HidP_GetValueCaps(int reportType, IntPtr caps, ref ushort length, IntPtr data);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr parent, int flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid guid, uint index,
            ref SP_DEVICE_INTERFACE_DATA data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data,
            IntPtr detail, int detailSize, out int requiredSize, IntPtr devInfo);
        [DllImport("setupapi.dll")]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
            uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadFile(SafeFileHandle h, IntPtr buf, int len, out int read, IntPtr ov);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool WriteFile(SafeFileHandle h, IntPtr buf, int len, out int written, IntPtr ov);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetOverlappedResult(SafeFileHandle h, IntPtr ov, out int transferred, bool wait);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CancelIoEx(SafeFileHandle h, IntPtr ov);
    }
}
