namespace HADA.Service.Settings;

public sealed class EntityOptions
{
    public const string SectionName = "Entities";

    /// <summary>Ids of entities that are not exposed to Home Assistant.</summary>
    public List<string> Disabled { get; set; } = [];
}
