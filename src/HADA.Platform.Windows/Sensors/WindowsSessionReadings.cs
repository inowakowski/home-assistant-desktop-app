using HADA.Core.Abstractions;

namespace HADA.Platform.Windows.Sensors;

/// <summary>What the session's sensors read, from Windows.</summary>
public sealed class WindowsSessionReadings : ISessionReadings
{
    public TimeSpan? ReadIdleTime() => UserInput.TryReadIdleTime();

    public IReadOnlyList<string>? AppsUsing(CaptureDevice device) =>
        MediaCapture.AppsUsing(device == CaptureDevice.Camera ? MediaCapture.Camera : MediaCapture.Microphone);
}
