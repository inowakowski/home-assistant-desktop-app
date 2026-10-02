using HADA.Core.Entities;

namespace HADA.Service.Settings;

public sealed class EntityOptions
{
    public const string SectionName = "Entities";

    /// <summary>Ids of entities that are not exposed to Home Assistant.</summary>
    public List<string> Disabled { get; set; } = [];

    /// <summary>
    /// Ids of the entities that are off unless listed here, such as the buttons that shut the computer down.
    /// </summary>
    public List<string> Enabled { get; set; } = [];

    public EntityFilter ToFilter() => new(Disabled, Enabled);
}
