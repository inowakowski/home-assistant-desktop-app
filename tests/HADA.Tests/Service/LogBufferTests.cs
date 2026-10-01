using HADA.Service.Logging;
using Microsoft.Extensions.Logging;

namespace HADA.Tests.Service;

public class LogBufferTests
{
    [Fact]
    public void Oldest_entries_are_dropped_when_the_buffer_is_full()
    {
        var buffer = Fill(new LogBuffer(capacity: 3), count: 5);

        Assert.Equal(new long[] { 3, 4, 5 }, buffer.GetAfter(0, 10).Select(entry => entry.Sequence));
    }

    [Fact]
    public void Reading_from_zero_returns_the_latest_entries_and_later_reads_page_forward()
    {
        var buffer = Fill(new LogBuffer(), count: 10);

        Assert.Equal(new long[] { 7, 8, 9, 10 }, buffer.GetAfter(0, 4).Select(entry => entry.Sequence));
        Assert.Equal(new long[] { 3, 4, 5 }, buffer.GetAfter(2, 3).Select(entry => entry.Sequence));
        Assert.Empty(buffer.GetAfter(10, 5));
    }

    [Fact]
    public void Long_messages_and_exceptions_are_truncated()
    {
        var buffer = new LogBuffer();

        buffer.Add(LogLevel.Error, "Test", new string('m', 5000), new InvalidOperationException(new string('e', 5000)));

        var entry = Assert.Single(buffer.GetAfter(0, 1));
        Assert.True(entry.Message.Length <= 1501);
        Assert.True(entry.Exception!.Length <= 1501);
    }

    private static LogBuffer Fill(LogBuffer buffer, int count)
    {
        for (var i = 1; i <= count; i++)
        {
            buffer.Add(LogLevel.Information, "Test", $"entry {i}", exception: null);
        }

        return buffer;
    }
}
