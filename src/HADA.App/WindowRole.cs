using System.Net.Sockets;
using HADA.Core;
using HADA.Core.Hosting;
using HADA.Platform.Windows.Desktop;

namespace HADA.App;

/// <summary>
/// One window per user and privilege level, as the previous window has it, under the same name: started again,
/// the window that is open comes to the front, and the tray app can ask it to close.
/// </summary>
internal sealed class WindowRole : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _name;
    private readonly List<AppSignal> _signals = [];

    private WindowRole(Mutex mutex, string name)
    {
        _mutex = mutex;
        _name = name;
    }

    /// <summary>
    /// Makes this process the one showing the window. Returns <see langword="null"/> when another process already
    /// is; that one is then asked to bring its window to the front.
    /// </summary>
    public static WindowRole? Take(bool elevated)
    {
        var name = AppInstance.WindowRoleName(elevated);
        var mutex = new Mutex(initiallyOwned: true, name, out var isFirst);
        if (isFirst)
        {
            return new WindowRole(mutex, name);
        }

        mutex.Dispose();
        if (OperatingSystem.IsWindows())
        {
            // This process was started by the user and may take the foreground; it passes that right on.
            Shell.LetAnotherComeToFront();
        }

        try
        {
            AppSignal.TrySet(name + ".Show");
        }
        catch (UnauthorizedAccessException)
        {
            // The window open is the administrator's, which this process may not signal.
        }

        return null;
    }

    /// <summary>Starts listening for being asked to come to the front, or to close.</summary>
    public void Serve(Action show, Action close)
    {
        Listen(".Show", show, once: false);

        // Asked by whoever is about to replace or remove the program, and cannot wait for an answer.
        Listen(".Close", close, once: true);
    }

    public void Dispose()
    {
        foreach (var signal in _signals)
        {
            signal.Dispose();
        }

        _mutex.Dispose();
    }

    private void Listen(string what, Action onSignal, bool once)
    {
        try
        {
            _signals.Add(AppSignal.Listen(_name + what, onSignal, once));
        }
        catch (Exception ex) when (ex is IOException or SocketException or UnauthorizedAccessException)
        {
            // Without it the window still works; it is only not found from outside.
        }
    }
}
