namespace HADA.Service.Settings;

public sealed class UpdateOptions
{
    public const string SectionName = "Updates";

    /// <summary>Whether the service asks GitHub for a newer version once a day. Asking on request works either way.</summary>
    public bool CheckAutomatically { get; set; } = true;

    /// <summary>Whether versions marked as pre-releases count as newer versions.</summary>
    public bool IncludePrereleases { get; set; } = true;
}
