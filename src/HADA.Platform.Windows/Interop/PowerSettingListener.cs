using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace HADA.Platform.Windows.Interop;

/// <summary>
/// Receives power setting changes (display on/off, lid) through a callback, which works in a service as well as in a
/// desktop process. Windows reports the current value right after registering, then every change.
/// </summary>
public sealed unsafe class PowerSettingListener : IDisposable
{
    /// <summary>0 = off, 1 = on, 2 = dimmed.</summary>
    public static readonly Guid ConsoleDisplayState = new("6FE69556-704A-47A0-8F24-C28D936FDA47");

    /// <summary>0 = closed, 1 = open.</summary>
    public static readonly Guid LidSwitchState = new("BA3E0F4D-B817-4094-A2D1-D56379E6A0F3");

    private const uint DeviceNotifyCallback = 2;
    private const uint PowerSettingChange = 0x8013;

    // POWERBROADCAST_SETTING: GUID PowerSetting, DWORD DataLength, UCHAR Data[].
    private const int DataLengthOffset = 16;
    private const int DataOffset = 20;

    private readonly Action<Guid, int> _onChange;
    private readonly List<nint> _registrations = [];
    private GCHandle _self;

    /// <param name="onChange">Called on a system thread with the setting and its new value; must not block.</param>
    public PowerSettingListener(Action<Guid, int> onChange)
    {
        _onChange = onChange;
        _self = GCHandle.Alloc(this);
    }

    /// <summary>Returns <see langword="false"/> and the Win32 error code when the setting cannot be watched.</summary>
    public bool TryRegister(Guid setting, out uint error)
    {
        ObjectDisposedException.ThrowIf(!_self.IsAllocated, this);

        var parameters = new DeviceNotifySubscribeParameters
        {
            Callback = &OnNotification,
            Context = GCHandle.ToIntPtr(_self),
        };
        error = NativeMethods.PowerSettingRegisterNotification(in setting, DeviceNotifyCallback, &parameters, out var registration);
        if (error != 0)
        {
            return false;
        }

        _registrations.Add(registration);
        return true;
    }

    public void Dispose()
    {
        foreach (var registration in _registrations)
        {
            _ = NativeMethods.PowerSettingUnregisterNotification(registration);
        }

        _registrations.Clear();
        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint OnNotification(nint context, uint type, nint setting)
    {
        try
        {
            if (type == PowerSettingChange
                && setting != 0
                && Marshal.ReadInt32(setting, DataLengthOffset) >= sizeof(int)
                && GCHandle.FromIntPtr(context).Target is PowerSettingListener listener)
            {
                listener._onChange(Marshal.PtrToStructure<Guid>(setting), Marshal.ReadInt32(setting, DataOffset));
            }
        }
        catch (Exception)
        {
            // An exception must never cross back into the system's notification thread.
        }

        return 0;
    }
}
