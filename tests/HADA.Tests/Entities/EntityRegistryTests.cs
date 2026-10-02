using HADA.Core.Entities;
using HADA.Core.Messaging;

namespace HADA.Tests.Entities;

public class EntityRegistryUnregisterTests
{
    [Fact]
    public async Task Unregister_removes_the_entity_and_publishes_the_removal_after_the_registration()
    {
        await using var bus = new ChannelEventBus();
        var registry = new EntityRegistry(bus);
        await using var changes = bus.Subscribe<EntityRegistryChange>();
        var entity = new EntityDescriptor { Id = "room", Name = "Room", Kind = EntityKind.Sensor };

        await registry.RegisterAsync(entity);
        Assert.True(await registry.UnregisterAsync("room"));
        Assert.False(await registry.UnregisterAsync("room"));

        Assert.False(registry.TryGet("room", out _));
        Assert.True(changes.TryRead(out var first));
        Assert.Equal(new EntityRegistered(entity), first);
        Assert.True(changes.TryRead(out var second));
        Assert.Equal(new EntityUnregistered(entity), second);
        Assert.False(changes.TryRead(out _));
    }
}

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
