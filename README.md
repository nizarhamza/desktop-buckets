# Desktop Buckets

A "bucket" is a hybrid of a folder and a live desktop widget. It is backed by a real
folder on disk, but renders on the Windows desktop as a compact tile that surfaces its
most relevant files without being opened — in the spirit of Windows 11's Start-menu
category tiles, but desktop-native and file-driven.

Status: **v1 core working** (see [Roadmap](#roadmap)).

---

## Install

Download **`DesktopBuckets-Setup-<version>.exe`** from the
[latest release](https://github.com/nizarhamza/desktop-buckets/releases/latest) and run it.

- Per-user install — **no admin prompt**.
- Installs to `%LOCALAPPDATA%\Programs\Desktop Buckets`.
- Registers in **Settings → Apps → Installed apps** as "Desktop Buckets"; uninstall from
  there (or the Start-menu *Uninstall Desktop Buckets* shortcut) at any time.
- Optional "start automatically when I sign in" checkbox (a per-user logon
  scheduled task, `\DesktopBuckets-Autostart`, removed on uninstall).
- Uninstalling removes the app, the autostart entry and the "New Bucket" desktop
  right-click verb, then asks whether to also delete settings/logs — **your bucket
  folders and their files are never touched.**

### Updates

The running app checks GitHub Releases (~12 s after launch, then every 6 h) and, when a
newer build exists, shows a window with the release notes and **Update now / Remind me
later / Skip this version**. "Update now" downloads the setup `.exe`, runs it silently,
and the app restarts on the new version. There's also **Check for updates…** in the tray
menu.

Two channels:

| Channel | Source | Default |
|---|---|---|
| `nightly` | the rolling build published on **every push to `main`** (`0.1.<run>`) | ✔ |
| `stable` | the latest `v*` tagged release | |

Override defaults with `%USERPROFILE%\Desktop Buckets\.app\update.json` (all keys
optional; the `.app` folder is hidden — the Settings window also shows its location):

```json
{
  "enabled": true,
  "repo": "nizarhamza/desktop-buckets",
  "channel": "nightly",
  "checkIntervalHours": 6,
  "token": null
}
```

`token` is only needed if the repo is private — a fine-grained PAT with *Contents:
Read-only*, stored on the machine, never in the binary.

Before a downloaded installer is run, the app checks that it carries an intact
Authenticode signature from the project certificate (thumbprint pinned in
`UpdateService.InstallerSignerThumbprint`, matching `packaging/DesktopBuckets.cer`).
An unsigned, tampered or differently-signed file is deleted and never launched, whatever
`repo` says. Switching channel (nightly ↔ stable) offers that channel's latest build even
if its version number is lower, since nightly build numbers climb past stable tags.

---

## What works today

- **Tray app**, single instance. A second launch forwards its command line to the
  running instance over a named pipe and exits.
- **Buckets are real folders.** Each lives at `%USERPROFILE%\Desktop Buckets\<Name>\`
  with a hidden `.bucket.json` holding its state. Anything dropped into the folder via
  File Explorer shows up in the tile.
- **Tile rendering:** rounded translucent card, an *N*-slot icon grid (default 2×2),
  real shell icons via `SHGetFileInfo`, the bucket label, and a `+N` overflow badge.
- **File selection & order:** pinned files first (in pin order), remaining slots filled
  by most-recent activity, newest first.
- **Live updates:** a debounced `FileSystemWatcher` per bucket folder — Explorer drops,
  renames, deletes and edits all reflect within ~250 ms.
- **Desktop-widget behaviour:** borderless, transparent, `WS_EX_NOACTIVATE`, held at the
  bottom of the Z-order so ordinary windows always sit on top while the tile stays
  visible on "Show desktop". Never steals focus.
- **Drag & drop** from Explorer onto a tile **moves** the item into the bucket (hold
  **Ctrl** to copy instead).
- **Desktop-aware** — a tile occupies whole desktop icon cells (the lattice Windows
  itself snaps icons to, per monitor) and hides/shows together with the desktop's "Show
  desktop icons" toggle. Icons under a tile are nudged one cell aside, phone-style,
  along the shortest chain to a free cell — only once the tile has rested there for a
  second, never while it is moving — and slide back when it leaves. A bucket is
  represented only by its tile: its backing folder is never a second icon on the desktop.
- **Per-file context menu:** Open · Pin/Unpin · Open file location · Copy path.
- **Tile context menu:** Open folder · Rename · Icon-slot count (1–9) · Lock position ·
  New bucket · toggle the desktop right-click entry · Delete (to Recycle Bin).
- **Explorer right-click → "New Bucket"** in the **Windows 11 main context menu**
  (desktop background and any folder), via a signed sparse-MSIX `IExplorerCommand`
  handler (`native/ShellExt/`). Toggle it from the tray / a tile context menu — one
  elevation prompt to trust the bundled dev certificate. Builds without the package
  fall back to an HKCU verb under *Show more options*.

## Interaction reference

| Action | Result |
|---|---|
| Double-click a tile icon | Opens that file directly |
| Double-click the tile body / label | Opens the bucket folder in File Explorer |
| Right-click a tile icon | Pin / Unpin / reveal / copy path |
| Right-click the tile body | Rename, delete, slots, lock, shell toggle, new bucket, Settings… |
| Drag the tile body | Moves the tile; snaps to the desktop icon cells; position persisted. Icons under it are nudged aside only once the tile has rested there for a second (or is dropped) and slide back when it moves on |
| Drop files on the tile | **Moves** them into the bucket (Ctrl = copy) |

---

## Design decisions

### Scope: Fences-style overlay, not a namespace extension

The tiles are borderless top-level windows, not a shell **namespace / icon-handler**
extension hooking `explorer.exe` (fragile across Windows updates, out of scope).

The **context-menu** entry *is* a real shell extension: an `IExplorerCommand` COM
handler (`native/ShellExt/dllmain.cpp`, C++/WRL) registered through a **signed sparse
MSIX package** (`packaging/`) — the only supported route onto the Windows 11 main
context menu. Explorer hosts the DLL in a COM surrogate; `Invoke` shells out to
`DesktopBuckets.exe --new-bucket "<folder>"`. It is not inside the "New ▸" flyout (that
needs a `ShellNew` handler, awkward for folders).

Enabling it needs one elevation prompt to trust the bundled self-signed dev
certificate (`packaging/DesktopBuckets.cer`). CI signs the package from the
`SIGNING_PFX_BASE64` / `SIGNING_PFX_PASSWORD` repo secrets; without them the workflow
still builds, shipping only the legacy fallback verb.

### Open questions from the spec — resolved (all revisitable)

| Question | v1 decision | Rationale |
|---|---|---|
| "Recently worked" definition | `max(LastWriteTime, last-opened-via-tile)`. The tile stamps a timestamp when *it* launches a file. | Reliable, no NTFS last-access dependency, no global launch hook |
| Refresh behaviour | Live. `FileSystemWatcher` per folder, 250 ms debounce | Matches how Explorer feels |
| Pin limit / order | Unlimited pins; only the top *N* show; order = order pinned (oldest first), new pins appended | Predictable; an old reliable pin is never bumped off by a newer one |
| Missing / moved file | Dropped from the tile silently, next candidate promoted. A pinned-but-missing file stays in `.bucket.json` (so a briefly-offline path can reappear) and is pruned on next startup if still gone | No surprises, tolerant of network paths |
| Slot count | Default 4; 1–9 per bucket via the tile context menu | — |

### The tile is the bucket, not a folder

New buckets are created in `%USERPROFILE%\Desktop Buckets\` — never as a folder on the
actual desktop — so there is never a duplicate folder icon next to the tile. If a
bucket's backing folder *is* found sitting directly on the desktop (e.g. you made a
bucket there, or moved the folder), the app marks that folder hidden so the tile stays
its sole representation. "Open bucket folder" still opens it.

### Storage

App state lives in a hidden `.app` folder under the bucket root, `%USERPROFILE%\Desktop
Buckets\.app` (not `%APPDATA%`: when the exe runs with the shell package's identity,
Windows redirects AppData to a per-package folder, which would silently split state).

- `%USERPROFILE%\Desktop Buckets\.app\buckets.json` — just the list of bucket folder paths.
- `<bucket folder>\.bucket.json` (hidden) — id, display name, pin list, last-opened map,
  slot count, window position, lock flag. Keeping state *in the folder* makes a bucket
  portable.
- `%USERPROFILE%\Desktop Buckets\.app\log.txt` — best-effort rolling diagnostic log (512 KB cap).
- `%USERPROFILE%\Desktop Buckets\.app\update.json` / `update-state.json` — updater
  settings and state.

All JSON is written temp-file-then-rename, so an interrupted save leaves the previous
file intact rather than a truncated one.

---

## Build & run

Requires the **.NET 8 SDK** (`dotnet --version` ≥ 8.0). No Visual Studio needed.

```bash
dotnet build src/DesktopBuckets/DesktopBuckets.csproj
```

```bash
dotnet run --project src/DesktopBuckets/DesktopBuckets.csproj
```

The app starts in the system tray. Right-click the tray icon → **New bucket…**.

### Building the installer

Requires [Inno Setup 6](https://jrsoftware.org/isdl.php) (`winget install JRSoftware.InnoSetup`).

```bash
dotnet publish src/DesktopBuckets/DesktopBuckets.csproj -c Release -r win-x64 --self-contained true -p:DebugType=none -o publish
iscc installer/DesktopBuckets.iss
```

Output: `installer/Output/DesktopBuckets-Setup-<version>.exe`.

[`.github/workflows/release.yml`](.github/workflows/release.yml) does this on a runner:

- **push to `main`** → version `0.1.<run>`, replaces the `nightly` prerelease with the new installer
- **push a `v*` tag** → that version, creates/updates the matching stable release

### Command-line

| Command | Effect |
|---|---|
| `DesktopBuckets.exe` | Start (or focus the running instance) |
| `DesktopBuckets.exe --new-bucket "C:\some\folder"` | Create a bucket (prompts for a name); used by the Explorer verb |
| `DesktopBuckets.exe --register-shell` / `--unregister-shell` | Add / remove the Explorer right-click entry, then exit |

---

## Project layout

```
src/DesktopBuckets/
  App.xaml(.cs)              Startup, single-instance, CLI, global exception guard
  app.manifest              asInvoker, per-monitor-v2 DPI
  Interop/
    NativeMethods.cs         P/Invoke: SHGetFileInfo, SetWindowPos, SendMessageTimeout, …
    ShellFileOperations.cs   IFileOperation: drops (move/copy) + Recycle Bin, on an STA worker
    Authenticode.cs          WinVerifyTrust + signer thumbprint for downloaded installers
    DesktopWindowHelper.cs   WS_EX_NOACTIVATE + hold-at-bottom Z-order
    AcrylicHelper.cs         blur-behind for the translucent tiles (toggleable)
    WindowChromeHelper.cs    dark/light title bar + rounded corners, tracks the theme
  Themes/
    Palette.Dark.xaml        the two swappable colour palettes (matching keys)
    Palette.Light.xaml
    Controls.xaml            Win11-style styles: toggle switch, combo, slider, menus, …
  Models/
    BucketConfig.cs          the .bucket.json shape
    AppearanceConfig.cs      appearance.json: theme, tile transparency/corners/blur, accent
    BucketFile.cs            immutable snapshot of one file + activity key
    Bucket.cs                folder + config; enumerate / pin / rename / prune
  Services/
    BucketStore.cs           the buckets.json index
    AppearanceService.cs     loads appearance.json; resolves System theme + Windows accent
    ThemeManager.cs          swaps the light/dark palette dictionary at runtime
    BucketManager.cs         owns windows + watchers + tray; implements IBucketHost
    BucketWatcher.cs         debounced FileSystemWatcher
    FileRankingService.cs    pinned-first + recent-fill selection (pure, testable)
    IconService.cs           SHGetFileInfo → frozen ImageSource, cached
    ShellIntegration.cs      MSIX package register/unregister (+ legacy verb fallback)
    StartupRegistration.cs   sign-in autostart: the \DesktopBuckets-Autostart logon task
    UpdateService.cs         GitHub-release update poller + installer hand-off
    SingleInstance.cs        mutex + named-pipe command forwarding
    RecycleBin.cs            thin wrapper over ShellFileOperations.Recycle
    JsonUtil.cs / Log.cs
  ViewModels/
    BucketTileViewModel.cs   slots, grid dims, empty/overflow state, file actions
    BucketFileViewModel.cs   one slot
  Views/
    BucketTileWindow.xaml(.cs)   the tile (ApplyAppearance: glass opacity/corner/blur/label colour)
    SettingsWindow.xaml(.cs)     General / Appearance / About, sidebar nav, live-applied
    InputDialog.xaml(.cs)        name prompt
    UpdatePromptWindow.xaml(.cs) update notice
    TrayIconController.cs        WinForms NotifyIcon

native/ShellExt/
  dllmain.cpp                IExplorerCommand handler (C++/WRL) -> DesktopBuckets.ShellExt.dll
  ShellExt.def / build.cmd
packaging/
  AppxManifest.xml           sparse MSIX manifest (windows.fileExplorerContextMenus)
  make-logos.ps1             generates the package's PNG logos
  build-package.ps1          makeappx pack + signtool sign
  DesktopBuckets.cer         public dev cert (the private .pfx is a CI secret)
```

---

## Roadmap

Not yet implemented / rough edges:

- **First-class pin reordering** (drag pins within the tile; today pin order = the order
  you pinned them).
- **WorkerW wallpaper parenting** as an option, for tiles that should hide on "Show
  desktop" like real desktop icons. v1 keeps them visible (Fences-style).
- **Multi-monitor position clamping** on display-configuration changes is basic.
- **Size presets / density** (compact vs comfortable). Theme (light/dark/system), tile
  transparency, corner roundness, blur and accent colour now live in Settings › Appearance.
- **Delta updates** — the updater currently pulls the full ~49 MB installer each time.
- **UI tests** — `tests/DesktopBuckets.Tests` covers the pure logic (ranking, pin/prune,
  version parsing, JSON persistence); the tile windows and interop are untested.
