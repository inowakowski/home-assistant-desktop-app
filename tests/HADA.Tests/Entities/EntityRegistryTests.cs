using HADA.Core.Entities;
using HADA.Core.Messaging;

namespace HADA.Tests.Entities;

public class EntityRegistryTests
{
    private static EntityDescriptor Sensor(string id) => new() { Id = id, Name = "Test", Kind = EntityKind.Sensor };

    [Fact]
    public async Task RegisterAsync_stores_entity_and_announces_it_on_the_bus()
    {
        await using var bus = new ChannelEventBus();
        await using var announcements = bus.Subscribe<EntityRegistered>();
        var registry = new EntityRegistry(bus);

        var entity = Sensor("cpu_load");
        await registry.RegisterAsync(entity);

        Assert.True(registry.TryGet("cpu_load", out var stored));
        Assert.Same(entity, stored);
        Assert.True(announcements.TryRead(out var announcement));
        Assert.Same(entity, announcement.Entity);
    }

    [Theory]
    [InlineData("")]
    [InlineData("CPU")]
    [InlineData("cpu/load")]
    [InlineData("cpu load")]
    [InlineData("cpu#")]
    public async Task RegisterAsync_rejects_ids_unsafe_for_topics(string id)
    {
        await using var bus = new ChannelEventBus();
        var registry = new EntityRegistry(bus);

        await Assert.ThrowsAsync<ArgumentException>(() => registry.RegisterAsync(Sensor(id)).AsTask());
        Assert.Empty(registry.Entities);
    }
}
