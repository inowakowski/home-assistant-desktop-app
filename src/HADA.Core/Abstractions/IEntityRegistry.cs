using System.Diagnostics.CodeAnalysis;
using HADA.Core.Entities;

namespace HADA.Core.Abstractions;

/// <summary>
/// Entities this computer exposes to Home Assistant. Sensors and actions register here;
/// engines read the registry when (re)connecting and follow <see cref="EntityRegistryChange"/> for later changes.
/// </summary>
public interface IEntityRegistry
{
    IReadOnlyCollection<EntityDescriptor> Entities { get; }

    /// <summary>Adds or replaces an entity and publishes <see cref="EntityRegistered"/> on the event bus.</summary>
    ValueTask RegisterAsync(EntityDescriptor entity, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes an entity and publishes <see cref="EntityUnregistered"/> on the event bus.
    /// Returns <see langword="false"/> when there is no entity with that id.
    /// </summary>
    ValueTask<bool> UnregisterAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks a registered entity as available or not and, if that is a change, publishes
    /// <see cref="EntityAvailabilityChanged"/> on the event bus. Entities are available until told otherwise.
    /// </summary>
    ValueTask SetAvailabilityAsync(string id, bool isAvailable, CancellationToken cancellationToken = default);

    /// <summary>Whether the entity's source is there to report values. True for unknown ids.</summary>
    bool IsAvailable(string id);

    bool TryGet(string id, [MaybeNullWhen(false)] out EntityDescriptor entity);
}
