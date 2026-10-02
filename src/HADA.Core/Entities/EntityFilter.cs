using System.Collections.Frozen;
using HADA.Core.Abstractions;

namespace HADA.Core.Entities;

/// <summary>
/// Exposes every entity except those disabled in settings. Entities that are off by default
/// (<see cref="EntityDescriptor.EnabledByDefault"/>) are exposed only when enabled in settings.
/// </summary>
public sealed class EntityFilter(IEnumerable<string> disabledEntityIds, IEnumerable<string>? enabledEntityIds = null) : IEntityFilter
{
    private readonly FrozenSet<string> _disabled = disabledEntityIds.ToFrozenSet(StringComparer.Ordinal);
    private readonly FrozenSet<string> _enabled = (enabledEntityIds ?? []).ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Exposes everything, the off-by-default entities included.</summary>
    public static IEntityFilter AllEnabled { get; } = new Everything();

    public IReadOnlySet<string> DisabledEntityIds => _disabled;

    /// <summary>Ids of the off-by-default entities the user switched on.</summary>
    public IReadOnlySet<string> EnabledEntityIds => _enabled;

    public bool IsEnabled(EntityDescriptor entity) =>
        entity.EnabledByDefault ? !_disabled.Contains(entity.Id) : _enabled.Contains(entity.Id);

    private sealed class Everything : IEntityFilter
    {
        public bool IsEnabled(EntityDescriptor entity) => true;
    }
}
