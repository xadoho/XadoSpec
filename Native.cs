using System;
using System.Runtime.InteropServices;

namespace XadoSpec
{
    static class Native
    {
        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetPhysicallyInstalledSystemMemory(out ulong totalKb);

        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORYSTATUSEX
        {
            public uint dwLength, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint length);

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);

        [StructLayout(LayoutKind.Sequential)]
        public struct SYSTEM_INFO
        {
            public ushort wProcessorArchitecture, wReserved;
            public uint dwPageSize;
            public IntPtr lpMinimumApplicationAddress, lpMaximumApplicationAddress, dwActiveProcessorMask;
            public uint dwNumberOfProcessors, dwProcessorType, dwAllocationGranularity;
            public ushort wProcessorLevel, wProcessorRevision;
        }

        [DllImport("kernel32.dll")]
        public static extern void GetNativeSystemInfo(out SYSTEM_INFO info);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetFirmwareType(out int type);

        [StructLayout(LayoutKind.Sequential)]
        public struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
            public int BatteryLifeTime, BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

        [StructLayout(LayoutKind.Sequential)]
        public struct TPM_DEVICE_INFO
        {
            public uint structVersion, tpmVersion, tpmInterfaceType, tpmImpRevision;
        }

        [DllImport("tbs.dll")]
        public static extern uint Tbsi_GetDeviceInfo(uint size, out TPM_DEVICE_INFO info);

        [StructLayout(LayoutKind.Sequential)]
        public struct LUID { public uint Low; public int High; }

        [StructLayout(LayoutKind.Sequential)]
        public struct DC_RATIONAL { public uint Num, Den; }

        [StructLayout(LayoutKind.Sequential)]
        public struct DC_PATH_SOURCE
        {
            public LUID adapterId;
            public uint id, modeInfoIdx, statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DC_PATH_TARGET
        {
            public LUID adapterId;
            public uint id, modeInfoIdx, outputTechnology, rotation, scaling;
            public DC_RATIONAL refreshRate;
            public uint scanLineOrdering;
            public int targetAvailable;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DC_PATH
        {
            public DC_PATH_SOURCE source;
            public DC_PATH_TARGET target;
            public uint flags;
        }

        [StructLayout(LayoutKind.Explicit, Size = 64)]
        public struct DC_MODE
        {
            [FieldOffset(0)] public uint infoType;
            [FieldOffset(4)] public uint id;
            [FieldOffset(8)] public LUID adapterId;
            [FieldOffset(16)] public uint width;
            [FieldOffset(20)] public uint height;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DC_HEADER
        {
            public uint type, size;
            public LUID adapterId;
            public uint id;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DC_TARGET_NAME
        {
            public DC_HEADER header;
            public uint flags, outputTechnology;
            public ushort edidManufactureId, edidProductCodeId;
            public uint connectorInstance;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string friendlyName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string devicePath;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DC_SOURCE_NAME
        {
            public DC_HEADER header;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string gdiName;
        }

        [DllImport("user32.dll")]
        public static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);

        [DllImport("user32.dll")]
        public static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] DC_PATH[] paths, ref uint numModes, [Out] DC_MODE[] modes, IntPtr topology);

        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
        public static extern int GetTargetName(ref DC_TARGET_NAME request);

        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
        public static extern int GetSourceName(ref DC_SOURCE_NAME request);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor, rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
        }

        public delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);

        [DllImport("shcore.dll")]
        public static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    }
}
