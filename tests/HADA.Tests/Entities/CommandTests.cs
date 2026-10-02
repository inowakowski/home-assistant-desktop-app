using System.Text.Json;
using HADA.Core.Entities;
using HADA.Core.Models;

namespace HADA.Tests.Entities;

public class CommandTests
{
    private static readonly EntityDescriptor Volume = new() { Id = "volume_level", Name = "Volume", Kind = EntityKind.Number, Min = 0, Max = 100 };

    [Theory]
    [InlineData(EntityKind.Switch, "on", "on")]
    [InlineData(EntityKind.Switch, " OFF ", "off")]
    [InlineData(EntityKind.Number, "40", "40")]
    [InlineData(EntityKind.Number, "4e1", "40")]
    [InlineData(EntityKind.Number, "100.0", "100")]
    [InlineData(EntityKind.Notify, "Dinner is ready", "Dinner is ready")]
    [InlineData(EntityKind.Button, "anything", null)]
    public void Accepted_values_are_brought_into_one_form(EntityKind kind, string raw, string? expected)
    {
        Assert.True(CommandValue.TryNormalize(Volume with { Kind = kind }, raw, out var value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData(EntityKind.Switch, "toggle")]
    [InlineData(EntityKind.Switch, null)]
    [InlineData(EntityKind.Number, "101")]
    [InlineData(EntityKind.Number, "-1")]
    [InlineData(EntityKind.Number, "NaN")]
    [InlineData(EntityKind.Number, "4,5")]
    [InlineData(EntityKind.Number, "")]
    [InlineData(EntityKind.Notify, "  ")]
    [InlineData(EntityKind.Sensor, "1")]
    [InlineData(EntityKind.BinarySensor, "on")]
    public void Values_an_entity_does_not_accept_are_refused(EntityKind kind, string? raw)
    {
        Assert.False(CommandValue.TryNormalize(Volume with { Kind = kind }, raw, out _));
    }

    [Fact]
    public void A_notification_longer_than_the_limit_is_cut_off()
    {
        var notify = Volume with { Kind = EntityKind.Notify };

        Assert.True(CommandValue.TryNormalize(notify, new string('x', CommandValue.MaxMessageLength + 10), out var value));
        Assert.Equal(CommandValue.MaxMessageLength, value!.Length);
    }

    [Fact]
    public void Parameters_are_read_whether_they_are_text_or_json()
    {
        using var json = JsonDocument.Parse("""{"title": "From the pipe", "count": 3}""");
        var command = new ActionCommand
        {
            ActionId = "notification",
            Parameters = new Dictionary<string, object?>
            {
                ["local"] = "In process",
                ["title"] = json.RootElement.GetProperty("title").Clone(),
                ["count"] = json.RootElement.GetProperty("count").Clone(),
            },
        };

        Assert.Equal("In process", command.GetParameter("local"));
        Assert.Equal("From the pipe", command.GetParameter("title"));
        Assert.Null(command.GetParameter("count"));
        Assert.Null(command.GetParameter("missing"));
    }

    [Fact]
    public void Entities_that_are_off_by_default_need_switching_on_and_the_others_switching_off()
    {
        var sensor = new EntityDescriptor { Id = "cpu_load", Name = "CPU", Kind = EntityKind.Sensor };
        var shutdown = new EntityDescriptor { Id = "shutdown", Name = "Shut down", Kind = EntityKind.Button, EnabledByDefault = false };

        var defaults = new EntityFilter([]);
        Assert.True(defaults.IsEnabled(sensor));
        Assert.False(defaults.IsEnabled(shutdown));

        var changed = new EntityFilter(["cpu_load"], ["shutdown"]);
        Assert.False(changed.IsEnabled(sensor));
        Assert.True(changed.IsEnabled(shutdown));

        // Listing an ordinary entity as enabled, or an off-by-default one as disabled, changes nothing.
        var redundant = new EntityFilter(["shutdown"], ["cpu_load"]);
        Assert.True(redundant.IsEnabled(sensor));
        Assert.False(redundant.IsEnabled(shutdown));

        Assert.True(EntityFilter.AllEnabled.IsEnabled(shutdown));
    }
}
