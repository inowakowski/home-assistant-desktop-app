# Building and running

## What you need

- [.NET SDK 10](https://dotnet.microsoft.com/download). The projects target .NET 9, and `global.json` pins SDK 10.0.401 or a newer feature band.
- For running from build output: the .NET 9 runtime. Self-contained builds don't need it.

## Build and test

```powershell
dotnet build
dotnet test
```


## Running during development

Start the service and the tray in two terminals:

```powershell
dotnet run --project src/HADA.Service
```

```powershell
dotnet run --project src/HADA.Tray
```

The tray opens its window straight away. The Overview page should show the service as running and the tray as connected.

On a computer where HADA is also installed, stop the installed service first (`sc.exe stop HADA` in an elevated terminal). A service started from a console refuses to start while another one is running: both would read the same settings and connect to the broker under the same ID, each throwing the other out. Exit the installed tray app as well, from its icon.

Tray command-line options:

| Option | Effect |
|---|---|
| `--background` | Start without opening the window, e.g. at sign-in |
| `--autostart` | Marks a start made by Windows at sign-in. The tray exits again if the user turned **Start with Windows** off |
| `--page overview\|connections\|entities\|custom\|settings\|logs` | Open the window on a specific page (`custom` is **Custom entities**) |
| `--exit` | Tell the tray app that is already running to exit; in a portable copy that stops its service too |
| `--dashboard` | Run as the [dashboard window](../window.md#the-dashboard-window) |
| `--settings` | Run as the window, without tray icon or sensors. This is how the tray opens the window, and how **Unlock editing** reopens it as administrator |


## Building the installers

```powershell
.\scripts\Publish-HADA.ps1
```

This creates `artifacts\installer\HADA-<version>-x64.msi` and `HADA-<version>-arm64.msi`, and next to them the [portable](../getting-started/portable.md) `HADA-<version>-<x64|arm64>-portable.zip`; either computer can build both architectures. The GitHub workflow in `.github/workflows/build.yml` builds the same two files and attaches them to each run. The installer project (WiX) is in `installer/` and is deliberately not part of the solution, so `dotnet build` stays fast.

## Building this documentation

The pages are Markdown files in `docs/`, built with [MkDocs Material](https://squidfunk.github.io/mkdocs-material/):

```powershell
pip install -r docs/requirements.txt
mkdocs serve
```

`mkdocs serve` shows the site at `http://127.0.0.1:8000` and reloads as you edit.
