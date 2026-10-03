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

A translation of a page is a file next to it with the language in its name: `index.md` in English, `index.pl.md` in Polish. A page without a translation shows in English. The languages and the translated names in the navigation are listed in `mkdocs.yml`.

## Building the landing page

The landing page is in `website/`: plain HTML and CSS, one folder per language.

```powershell
python scripts/build_site.py
python -m http.server 8001 -d dist
```

The first command builds it into `dist/`, the second shows it at `http://localhost:8001`; its links to the documentation lead to `mkdocs serve`.

## Publishing

Cloudflare Pages publishes both from the `main` branch, as two projects:

| | Landing page | Documentation |
|---|---|---|
| Build command | `python scripts/build_site.py` | `pip install -r docs/requirements.txt && mkdocs build` |
| Output directory | `dist` | `site` |
| Variables | `SITE_URL`, `DOCS_URL` | `DOCS_SITE_URL`, `PYTHON_VERSION` |

`SITE_URL` is the landing page's address and `DOCS_URL` the documentation's, both without a slash at the end; `DOCS_SITE_URL` is the documentation's address with one.
