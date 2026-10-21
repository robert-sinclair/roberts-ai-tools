using System.Runtime.InteropServices;
using System.Text;

namespace rMic.Core.Devices;

/// <summary>
/// SetupAPI / CfgMgr32 interop. This is the same mechanism Device Manager uses when you
/// right-click a device and choose Enable/Disable, so the effect is identical: the device
/// is torn down and marked disabled in the registry, persisting across reboots.
/// </summary>
internal static class NativeMethods
{
    /// <summary>"Audio inputs and outputs" in Device Manager. Endpoints live here.</summary>
    internal static readonly Guid GuidDevClassAudioEndpoint = new("c166523c-fe0c-4a94-a586-f1a80cfbbf3e");

    internal static readonly IntPtr InvalidHandleValue = new(-1);

    internal const uint DIGCF_PRESENT = 0x00000002;

    internal const uint DIF_PROPERTYCHANGE = 0x00000012;

    internal const uint DICS_ENABLE = 0x00000001;
    internal const uint DICS_DISABLE = 0x00000002;

    internal const uint DICS_FLAG_GLOBAL = 0x00000001;
    internal const uint DICS_FLAG_CONFIGSPECIFIC = 0x00000002;

    internal const uint SPDRP_DEVICEDESC = 0x00000000;
    internal const uint SPDRP_FRIENDLYNAME = 0x0000000C;

    /// <summary>CM_Get_DevNode_Status: the devnode reported a problem code.</summary>
    internal const uint DN_HAS_PROBLEM = 0x00000400;

    /// <summary>The device is disabled (the problem code Device Manager sets on Disable).</summary>
    internal const uint CM_PROB_DISABLED = 22;

    internal const uint CR_SUCCESS = 0;

    internal const int ERROR_NO_MORE_ITEMS = 259;
    internal const int ERROR_INSUFFICIENT_BUFFER = 122;

    [StructLayout(LayoutKind.Sequential)]
    internal struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SP_CLASSINSTALL_HEADER
    {
        public uint cbSize;
        public uint InstallFunction;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SP_PROPCHANGE_PARAMS
    {
        public SP_CLASSINSTALL_HEADER ClassInstallHeader;
        public uint StateChange;
        public uint Scope;
        public uint HwProfile;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetClassDevsW")]
    internal static extern IntPtr SetupDiGetClassDevs(
        ref Guid classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiEnumDeviceInfo(
        IntPtr deviceInfoSet, uint memberIndex, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetDeviceInstanceIdW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiGetDeviceInstanceId(
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfoData,
        StringBuilder? deviceInstanceId,
        uint deviceInstanceIdSize,
        out uint requiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetDeviceRegistryPropertyW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiGetDeviceRegistryProperty(
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfoData,
        uint property,
        out uint propertyRegDataType,
        byte[]? propertyBuffer,
        uint propertyBufferSize,
        out uint requiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiSetClassInstallParamsW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiSetClassInstallParams(
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfoData,
        ref SP_PROPCHANGE_PARAMS classInstallParams,
        int classInstallParamsSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiCallClassInstaller(
        uint installFunction, IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("cfgmgr32.dll", SetLastError = true)]
    internal static extern uint CM_Get_DevNode_Status(
        out uint status, out uint problemNumber, uint devInst, uint flags);
}
