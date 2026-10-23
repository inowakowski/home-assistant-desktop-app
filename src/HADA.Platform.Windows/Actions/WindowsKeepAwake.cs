using System.Runtime.InteropServices;
using HADA.Core.Abstractions;
using HADA.Platform.Windows.Interop;

namespace HADA.Platform.Windows.Actions;

/// <summary>
/// Holds sleep off with a power request, as a video player or Windows Update does: <c>powercfg /requests</c> lists
/// it, with the reason below, and Windows drops it when the service ends. The screen may still turn off.
/// </summary>
public sealed class WindowsKeepAwake : IKeepAwake, IDisposable
{
    private const string Reason = "Home Assistant keeps this computer awake (HADA)";

    // POWER_REQUEST_CONTEXT_VERSION, POWER_REQUEST_CONTEXT_SIMPLE_STRING and PowerRequestSystemRequired.
    private const uint ContextVersion = 0;
    private const uint SimpleString = 0x1;
    private const int SystemRequired = 1;

    private readonly Lock _lock = new();
    private nint _request;
    private nint _reason;

    public string? Hold()
    {
        lock (_lock)
        {
            if (_request == 0)
            {
                // Kept for as long as the request is: Windows shows it from there.
                _reason = Marshal.StringToHGlobalUni(Reason);
                var context = new ReasonContext { Version = ContextVersion, Flags = SimpleString, SimpleReasonString = _reason };
                var request = NativeMethods.PowerCreateRequest(ref context);
                if (request is 0 or -1)
                {
                    var error = Marshal.GetLastPInvokeError();
                    Marshal.FreeHGlobal(_reason);
                    _reason = 0;
                    return $"Windows did not make a power request (error {error}).";
                }

                _request = request;
            }

            return NativeMethods.PowerSetRequest(_request, SystemRequired)
                ? null
                : $"Windows did not hold sleep off (error {Marshal.GetLastPInvokeError()}).";
        }
    }

    public void Release()
    {
        lock (_lock)
        {
            if (_request != 0)
            {
                NativeMethods.PowerClearRequest(_request, SystemRequired);
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_request != 0)
            {
                NativeMethods.PowerClearRequest(_request, SystemRequired);
                NativeMethods.CloseHandle(_request);
                _request = 0;
            }

            if (_reason != 0)
            {
                Marshal.FreeHGlobal(_reason);
                _reason = 0;
            }
        }
    }
}
