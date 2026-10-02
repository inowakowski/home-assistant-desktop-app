using HADA.Ipc;

namespace HADA.Service.Settings;

public sealed class CustomSensorOptions
{
    public const string SectionName = "CustomSensors";

    /// <summary>Sensors defined by the user; see <see cref="CustomSensorDefinition"/>.</summary>
    public List<CustomSensorDefinition> Items { get; set; } = [];
}
