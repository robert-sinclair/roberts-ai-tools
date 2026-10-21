using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace rMic.Core.Devices;

/// <summary>
/// Finds audio capture endpoints and switches them on or off at the driver level.
/// Every mutating call requires the calling process to be elevated, which is why this
/// only ever runs inside the LocalSystem service.
/// </summary>
public sealed class CaptureDeviceManager
{
    private readonly ServiceConfig _config;
    private readonly Action<string> _log;
    private readonly Regex[] _include;
    private readonly Regex[] _exclude;

    public CaptureDeviceManager(ServiceConfig config, Action<string>? log = null)
    {
        _config = config;
        _log = log ?? (_ => { });
        _include = Compile(config.CaptureInstanceIdPatterns);
        _exclude = Compile(config.ExcludeInstanceIdPatterns);
    }

    private Regex[] Compile(IEnumerable<string> patterns)
    {
        var compiled = new List<Regex>();
        foreach (var pattern in patterns)
        {
            try
            {
                compiled.Add(new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
            }
            catch (ArgumentException ex)
            {
                _log($"Ignoring invalid device pattern '{pattern}': {ex.Message}");
            }
        }
        return compiled.ToArray();
    }

    private IEnumerable<Guid> ClassGuids()
    {
        yield return NativeMethods.GuidDevClassAudioEndpoint;

        foreach (var raw in _config.ExtraDeviceClassGuids)
        {
            if (Guid.TryParse(raw, out var parsed))
                yield return parsed;
            else
                _log($"Ignoring invalid device class GUID '{raw}' in config.");
        }
    }

    /// <summary>Read-only snapshot of every capture device matching the configured patterns.</summary>
    public List<CaptureDevice> Enumerate()
    {
        var found = new List<CaptureDevice>();
        foreach (var classGuid in ClassGuids())
        {
            Walk(classGuid, (IntPtr set, ref NativeMethods.SP_DEVINFO_DATA data, CaptureDevice device) =>
            {
                found.Add(device);
            });
        }
        return found;
    }

    /// <summary>
    /// Switches every matching capture device to <paramref name="enable"/>.
    /// </summary>
    /// <param name="restrictTo">
    /// When supplied, only these instance IDs are touched. Used on re-enable so rMic only
    /// restores devices it switched off itself, and leaves devices the user had already
    /// disabled in Device Manager alone.
    /// </param>
    public ApplyResult Apply(bool enable, IEnumerable<string>? restrictTo = null)
    {
        var only = restrictTo is null
            ? null
            : new HashSet<string>(restrictTo, StringComparer.OrdinalIgnoreCase);

        var result = new ApplyResult();

        foreach (var classGuid in ClassGuids())
        {
            Walk(classGuid, (IntPtr set, ref NativeMethods.SP_DEVINFO_DATA data, CaptureDevice device) =>
            {
                if (only is not null && !only.Contains(device.InstanceId))
                    return;

                if (device.StatusKnown && device.IsEnabled == enable)
                {
                    result.AlreadyInState.Add(device);
                    return;
                }

                if (TryChangeState(set, ref data, enable, out var error))
                    result.Changed.Add(device);
                else
                    result.Failed.Add(new DeviceFailure(device, error));
            });
        }

        return result;
    }

    private delegate void DeviceVisitor(
        IntPtr deviceInfoSet, ref NativeMethods.SP_DEVINFO_DATA data, CaptureDevice device);

    private void Walk(Guid classGuid, DeviceVisitor visit)
    {
        var set = NativeMethods.SetupDiGetClassDevs(
            ref classGuid, null, IntPtr.Zero, NativeMethods.DIGCF_PRESENT);

        if (set == NativeMethods.InvalidHandleValue)
        {
            _log($"SetupDiGetClassDevs failed for class {classGuid:B}: {LastError()}");
            return;
        }

        try
        {
            var size = (uint)Marshal.SizeOf<NativeMethods.SP_DEVINFO_DATA>();
            for (uint index = 0; ; index++)
            {
                var data = new NativeMethods.SP_DEVINFO_DATA { cbSize = size };
                if (!NativeMethods.SetupDiEnumDeviceInfo(set, index, ref data))
                {
                    var err = Marshal.GetLastWin32Error();
                    if (err != NativeMethods.ERROR_NO_MORE_ITEMS)
                        _log($"SetupDiEnumDeviceInfo stopped at index {index}: {new Win32Exception(err).Message}");
                    break;
                }

                var instanceId = GetInstanceId(set, ref data);
                if (instanceId is null || !Matches(instanceId))
                    continue;

                var enabled = TryGetEnabled(data.DevInst, out var known);
                var name = GetName(set, ref data) ?? instanceId;

                visit(set, ref data, new CaptureDevice(instanceId, name, enabled, known));
            }
        }
        finally
        {
            NativeMethods.SetupDiDestroyDeviceInfoList(set);
        }
    }

    private bool Matches(string instanceId)
    {
        if (!_include.Any(r => r.IsMatch(instanceId)))
            return false;
        return !_exclude.Any(r => r.IsMatch(instanceId));
    }

    private static bool TryChangeState(
        IntPtr set, ref NativeMethods.SP_DEVINFO_DATA data, bool enable, out string error)
    {
        if (TryChangeStateInScope(set, ref data, enable, NativeMethods.DICS_FLAG_GLOBAL, out error))
            return true;

        // A few device stacks reject a global property change and only accept a change scoped
        // to the current hardware profile, so fall back rather than reporting failure outright.
        var globalError = error;
        if (TryChangeStateInScope(set, ref data, enable, NativeMethods.DICS_FLAG_CONFIGSPECIFIC, out error))
            return true;

        error = $"global scope: {globalError}; config-specific scope: {error}";
        return false;
    }

    private static bool TryChangeStateInScope(
        IntPtr set, ref NativeMethods.SP_DEVINFO_DATA data, bool enable, uint scope, out string error)
    {
        var parameters = new NativeMethods.SP_PROPCHANGE_PARAMS
        {
            ClassInstallHeader = new NativeMethods.SP_CLASSINSTALL_HEADER
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.SP_CLASSINSTALL_HEADER>(),
                InstallFunction = NativeMethods.DIF_PROPERTYCHANGE
            },
            StateChange = enable ? NativeMethods.DICS_ENABLE : NativeMethods.DICS_DISABLE,
            Scope = scope,
            HwProfile = 0
        };

        var ok = NativeMethods.SetupDiSetClassInstallParams(
            set, ref data, ref parameters, Marshal.SizeOf<NativeMethods.SP_PROPCHANGE_PARAMS>());

        if (!ok)
        {
            error = "SetupDiSetClassInstallParams: " + LastError();
            return false;
        }

        if (!NativeMethods.SetupDiCallClassInstaller(NativeMethods.DIF_PROPERTYCHANGE, set, ref data))
        {
            error = "SetupDiCallClassInstaller: " + LastError();
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool TryGetEnabled(uint devInst, out bool statusKnown)
    {
        var cr = NativeMethods.CM_Get_DevNode_Status(out var status, out var problem, devInst, 0);
        if (cr != NativeMethods.CR_SUCCESS)
        {
            statusKnown = false;
            // Assume enabled so a "disable everything" pass still attempts this device.
            return true;
        }

        statusKnown = true;
        var disabled = (status & NativeMethods.DN_HAS_PROBLEM) != 0 && problem == NativeMethods.CM_PROB_DISABLED;
        return !disabled;
    }

    private static string? GetInstanceId(IntPtr set, ref NativeMethods.SP_DEVINFO_DATA data)
    {
        NativeMethods.SetupDiGetDeviceInstanceId(set, ref data, null, 0, out var required);
        if (required == 0)
            return null;

        var buffer = new StringBuilder((int)required + 1);
        return NativeMethods.SetupDiGetDeviceInstanceId(set, ref data, buffer, (uint)buffer.Capacity, out _)
            ? buffer.ToString()
            : null;
    }

    private static string? GetName(IntPtr set, ref NativeMethods.SP_DEVINFO_DATA data)
        => GetStringProperty(set, ref data, NativeMethods.SPDRP_FRIENDLYNAME)
           ?? GetStringProperty(set, ref data, NativeMethods.SPDRP_DEVICEDESC);

    private static string? GetStringProperty(IntPtr set, ref NativeMethods.SP_DEVINFO_DATA data, uint property)
    {
        NativeMethods.SetupDiGetDeviceRegistryProperty(
            set, ref data, property, out _, null, 0, out var required);

        if (required == 0)
            return null;

        var buffer = new byte[required];
        if (!NativeMethods.SetupDiGetDeviceRegistryProperty(
                set, ref data, property, out _, buffer, required, out _))
            return null;

        var text = Encoding.Unicode.GetString(buffer).TrimEnd('\0');
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string LastError() => new Win32Exception(Marshal.GetLastWin32Error()).Message;
}
