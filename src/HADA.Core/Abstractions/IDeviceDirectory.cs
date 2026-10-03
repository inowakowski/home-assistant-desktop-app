namespace HADA.Core.Abstractions;

/// <summary>The hardware connected to this computer right now, as its operating system names it.</summary>
public interface IDeviceDirectory
{
    /// <summary>
    /// Whether a connected device's id contains <paramref name="idFragment"/>, ignoring case. On Windows that is a
    /// device instance id, so a fragment looks like <c>VID_0BDA&amp;PID_8153</c>.
    /// </summary>
    bool IsPresent(string idFragment);
}
