using System.Diagnostics.CodeAnalysis;
using HADA.Core.Entities;

namespace HADA.Core.Abstractions;

/// <summary>
/// Entities this computer exposes to Home Assistant. Sensors and actions register here;
/// engines read the registry when (re)connecting and follow <see cref="EntityRegistered"/> for later additions.
/// </summary>
public interface IEntityRegistry
{
    IReadOnlyCollection<EntityDescriptor> Entities { get; }

    /// <summary>Adds or replaces an entity and publishes <see cref="EntityRegistered"/> on the event bus.</summary>
    ValueTask RegisterAsync(EntityDescriptor entity, CancellationToken cancellationToken = default);

    bool TryGet(string id, [MaybeNullWhen(false)] out EntityDescriptor entity);
}
