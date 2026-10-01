using System.Collections.Frozen;
using HADA.Core.Abstractions;

namespace HADA.Core.Entities;

/// <summary>Exposes every entity except those disabled in settings.</summary>
public sealed class EntityFilter(IEnumerable<string> disabledEntityIds) : IEntityFilter
{
    private readonly FrozenSet<string> _disabled = disabledEntityIds.ToFrozenSet(StringComparer.Ordinal);

    public static EntityFilter AllEnabled { get; } = new([]);

    public IReadOnlySet<string> DisabledEntityIds => _disabled;

    public bool IsEnabled(string entityId) => !_disabled.Contains(entityId);
}
