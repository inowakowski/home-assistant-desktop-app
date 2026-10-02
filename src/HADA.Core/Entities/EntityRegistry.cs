using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using HADA.Core.Abstractions;

namespace HADA.Core.Entities;

public sealed partial class EntityRegistry(IEventBus bus) : IEntityRegistry
{
    private readonly ConcurrentDictionary<string, EntityDescriptor> _entities = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _unavailable = new(StringComparer.Ordinal);

    public IReadOnlyCollection<EntityDescriptor> Entities => _entities.Values.ToArray();

    public async ValueTask RegisterAsync(EntityDescriptor entity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);

        // Ids end up in MQTT topics and Home Assistant unique ids, so keep them to a safe character set.
        if (!IdPattern().IsMatch(entity.Id))
        {
            throw new ArgumentException(
                $"Entity id '{entity.Id}' must contain only lowercase letters, digits and underscores.", nameof(entity));
        }

        _entities[entity.Id] = entity;
        await bus.PublishAsync(new EntityRegistered(entity), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> UnregisterAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!_entities.TryRemove(id, out var entity))
        {
            return false;
        }

        _unavailable.TryRemove(id, out _);
        await bus.PublishAsync(new EntityUnregistered(entity), cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async ValueTask SetAvailabilityAsync(string id, bool isAvailable, CancellationToken cancellationToken = default)
    {
        if (!_entities.TryGetValue(id, out var entity))
        {
            return;
        }

        var changed = isAvailable ? _unavailable.TryRemove(id, out _) : _unavailable.TryAdd(id, 0);
        if (changed)
        {
            await bus.PublishAsync(new EntityAvailabilityChanged(entity, isAvailable), cancellationToken).ConfigureAwait(false);
        }
    }

    public bool IsAvailable(string id) => !_unavailable.ContainsKey(id);

    public bool TryGet(string id, [MaybeNullWhen(false)] out EntityDescriptor entity) =>
        _entities.TryGetValue(id, out entity);

    [GeneratedRegex("^[a-z0-9_]+$")]
    private static partial Regex IdPattern();
}
