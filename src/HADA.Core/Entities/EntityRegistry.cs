using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using HADA.Core.Abstractions;

namespace HADA.Core.Entities;

public sealed partial class EntityRegistry(IEventBus bus) : IEntityRegistry
{
    private readonly ConcurrentDictionary<string, EntityDescriptor> _entities = new(StringComparer.Ordinal);

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

    public bool TryGet(string id, [MaybeNullWhen(false)] out EntityDescriptor entity) =>
        _entities.TryGetValue(id, out entity);

    [GeneratedRegex("^[a-z0-9_]+$")]
    private static partial Regex IdPattern();
}
