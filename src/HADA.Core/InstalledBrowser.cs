namespace HADA.Core;

/// <param name="Name">As the browser calls itself, e.g. "Firefox".</param>
/// <param name="Path">Its program file.</param>
public sealed record InstalledBrowser(string Name, string Path);
