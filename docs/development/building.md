# Building and running

## What you need

- [.NET SDK 10](https://dotnet.microsoft.com/download). The projects target .NET 10, and `global.json` pins SDK 10.0.401 or a newer feature band.
- For running from build output: the .NET 10 runtime, which the SDK brings along. Self-contained builds don't need it.

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

The new window is the project `src/HADA.App`. The tray starts it from the `app` folder next to its own, as in an installed or portable copy, and opens the previous window when there is none, as when started with `dotnet run`. On Linux and macOS `HADA.App` is the whole program, window and agent: `dotnet run --project src/HADA.App` starts it, and it starts the service from the `service` folder beside it.

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

This creates `artifacts\installer\HADA-<version>-x64.msi` and `HADA-<version>-arm64.msi`, and next to them the [portable](../getting-started/portable.md) `HADA-<version>-<x64|arm64>-portable.zip`; either computer can build both architectures. The GitHub workflow in `.github/workflows/build.yml` builds the same files and attaches them to each run. The installer project (WiX) is in `installer/` and is deliberately not part of the solution, so `dotnet build` stays fast.

The packages for [Linux and macOS](../getting-started/linux-macos.md) are built in CI by `scripts/package-linux.sh` (an AppImage, a `.deb` and an `.rpm` for x64 and arm64) and `scripts/package-macos.sh` (a `.dmg` for Apple silicon and one for Intel), in the jobs `linux-packages` and `macos-packages`; each starts HADA from what it built, without a screen. Their names carry `-untested` until someone has tried them by hand, and the update check looks for both names (`HadaReleases.PackageNames`). The tests run on Windows, Ubuntu and macOS.

## Releases on GitHub

Every version is a GitHub release tagged `vX.Y.Z`, equal to `<Version>` in `Directory.Build.props`. A **stable** release is the `--latest` one: the landing page and the app's update check follow it. A **preview** is a pre-release titled `HADA X.Y.Z (preview)`, for a batch of features that someone should try before it becomes stable.

Which place of the number goes up says which kind of release it is (from 1.15.0 on):

| Release | Goes up | Example, after 1.15.0 |
|---|---|---|
| Preview | the third place | 1.15.1 (preview), 1.15.2 (preview) |
| Stable version | the second place, and the third goes back to 0 | 1.16.0 |
| A change that makes HADA anew, such as one that needs setting up again | the first place | 2.0.0 |

- A stable version brings what the previews since the last one carried. It is a release of its own, with the next number in the second place, made from `main` once the previews have been tried; a preview is not marked stable in place.
- A fix for a stable version, while no preview has come out since, is the next number in the third place, published as stable: 1.16.1. The number alone does not tell it from a preview; the title "(preview)" and the pre-release mark do. Once previews are out, a fix goes into the next one.
- A number that was published is never changed or used again: installed copies update only to a higher one.

The list is kept short by one rule: **one preview at a time**. A preview contains everything the earlier ones did, so:

- publishing a new preview deletes the previous one;
- publishing a stable version deletes every preview older than it.

Only the release is deleted, never the tag: `gh release delete vX.Y.Z --yes` without `--cleanup-tag`. An old version can still be built from its tag, and what it brought stays in the [changelog](../changelog.md), which keeps a heading for every version, previews included. Before deleting, save the release's notes and `SHA256SUMS.txt` (for example in `artifacts\release-archive\vX.Y.Z`, which is not tracked). The files themselves are lost with the release.

The update check in the app lists the 30 newest releases and takes the highest version, skipping pre-releases unless **Offer test versions too** is on, so deleting a superseded preview never hides an update.

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
