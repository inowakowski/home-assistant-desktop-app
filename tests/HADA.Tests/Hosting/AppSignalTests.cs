using System.Collections.Concurrent;
using HADA.Core.Hosting;

namespace HADA.Tests.Hosting;

public sealed class AppSignalTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly string _name = $@"Local\HADA.Test.{Guid.NewGuid():N}";

    // Short, as the path of a Unix socket must be.
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"hs-{Guid.NewGuid():N}"[..11]);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public async Task A_set_signal_reaches_its_listener_each_time()
    {
        var count = 0;
        var twice = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (AppSignal.Listen(_name, () =>
        {
            if (Interlocked.Increment(ref count) == 2)
            {
                twice.TrySetResult();
            }
        }))
        {
            Assert.True(AppSignal.TrySet(_name));
            await WaitUntilAsync(() => Volatile.Read(ref count) == 1);
            Assert.True(AppSignal.TrySet(_name));
            await twice.Task.WaitAsync(Timeout);
        }
    }

    [Fact]
    public void Nobody_listening_is_told_apart_from_a_signal_that_was_set()
    {
        Assert.False(AppSignal.TrySet(_name));

        using (AppSignal.Listen(_name, () => { }))
        {
        }

        Assert.False(AppSignal.TrySet(_name));
    }

    /// <summary>
    /// A disposed listener let go of its event a moment after Dispose returned, once in some thousand: the signal could
    /// still be opened, and set. Run often enough to show it, and as the processes of HADA do not, side by side.
    /// </summary>
    [WindowsFact]
    public void A_disposed_listener_leaves_its_signal_leading_nowhere_however_often_it_is_tried()
    {
        var names = Enumerable.Range(0, 4000).Select(i => $@"Local\HADA.Test.{i}.{Guid.NewGuid():N}").ToArray();
        var stillThere = new ConcurrentBag<string>();

        Parallel.ForEach(names, name =>
        {
            using (AppSignal.Listen(name, () => { }))
            {
            }

            if (AppSignal.TrySet(name))
            {
                stillThere.Add(name);
            }
        });

        Assert.Empty(stillThere);
    }

    [Fact]
    public async Task A_listener_disposed_from_its_own_signal_does_not_wait_for_itself_and_is_gone_after()
    {
        AppSignal? listener = null;
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        listener = AppSignal.Listen(_name, () =>
        {
            // Not before the field is set: the signal cannot come earlier than the test sets it.
            listener!.Dispose();
            disposed.TrySetResult();
        });

        Assert.True(AppSignal.TrySet(_name));
        await disposed.Task.WaitAsync(Timeout);

        // On Windows the event is closed as the callback returns.
        await WaitUntilAsync(() => !AppSignal.TrySet(_name));
    }

    [Fact]
    public async Task A_listener_for_once_hears_only_the_first_signal()
    {
        var count = 0;
        using (AppSignal.Listen(_name, () => Interlocked.Increment(ref count), once: true))
        {
            AppSignal.TrySet(_name);
            await WaitUntilAsync(() => Volatile.Read(ref count) == 1);
            AppSignal.TrySet(_name);
            await Task.Delay(200);
        }

        Assert.Equal(1, count);
    }

    // Unix sockets exist on Windows too, so what other systems use is tried on every one.
    [Fact]
    public async Task On_a_socket_each_connection_is_one_signal_and_the_socket_goes_with_its_listener()
    {
        var path = AppSignal.SocketPath(_folder, _name);
        Assert.False(AppSignal.SocketListener.TrySet(path));

        var count = 0;
        using (new AppSignal.SocketListener(path, () => Interlocked.Increment(ref count), once: false))
        {
            Assert.True(File.Exists(path));
            Assert.True(AppSignal.SocketListener.TrySet(path));
            Assert.True(AppSignal.SocketListener.TrySet(path));
            await WaitUntilAsync(() => Volatile.Read(ref count) == 2);
        }

        Assert.False(File.Exists(path));
        Assert.False(AppSignal.SocketListener.TrySet(path));
    }

    [Fact]
    public async Task A_socket_left_by_a_listener_that_did_not_end_tidily_is_taken_over()
    {
        var path = AppSignal.SocketPath(_folder, _name);
        Directory.CreateDirectory(_folder);
        await File.WriteAllTextAsync(path, "stale");
        Assert.False(AppSignal.SocketListener.TrySet(path));

        var signalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (new AppSignal.SocketListener(path, signalled.SetResult, once: true))
        {
            Assert.True(AppSignal.SocketListener.TrySet(path));
            await signalled.Task.WaitAsync(Timeout);
        }
    }

    [Fact]
    public void Socket_names_leave_out_what_only_windows_understands()
    {
        Assert.Equal(Path.Combine("run", "HADA.Tray.Exit.P1A2B3C4D.sock"), AppSignal.SocketPath("run", @"Local\HADA.Tray.Exit.P1A2B3C4D"));
    }

    [Fact]
    public void A_socket_name_too_long_for_a_socket_is_shortened_the_same_way_each_time()
    {
        var folder = Path.Combine(Path.GetTempPath(), new string('f', 60));
        var path = AppSignal.SocketPath(folder, @"Local\HADA.Window.Admin.P1A2B3C4D.Close");

        Assert.Equal(folder, Path.GetDirectoryName(path));
        Assert.True(Path.GetFileName(path).Length < 25);
        Assert.Equal(path, AppSignal.SocketPath(folder, @"Local\HADA.Window.Admin.P1A2B3C4D.Close"));
        Assert.NotEqual(path, AppSignal.SocketPath(folder, @"Local\HADA.Window.Admin.P1A2B3C4D.Show"));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        while (!condition())
        {
            await Task.Delay(20, timeout.Token);
        }
    }
}
