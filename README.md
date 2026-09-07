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
- Optional "start automatically when I sign in" checkbox (a per-user `Run` key,
  removed on uninstall).
- Uninstalling removes the app, the autostart entry and the "New Bucket" desktop
  right-click verb, then asks whether to also delete settings/logs — **your bucket
  folders and their files are never touched.**

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
- **Drag & drop** files from Explorer onto a tile to copy them into the bucket.
- **Per-file context menu:** Open · Pin/Unpin · Open file location · Copy path.
- **Tile context menu:** Open folder · Rename · Icon-slot count (1–9) · Lock position ·
  New bucket · toggle the desktop right-click entry · Delete (to Recycle Bin).
- **Explorer right-click → "New Bucket"** via an HKCU-only registry verb (no DLL, no
  elevation). Toggle it from the tray menu or a tile's context menu.

## Interaction reference

| Action | Result |
|---|---|
| Double-click a tile icon | Opens that file directly |
| Double-click the tile body / label | Opens the bucket folder in File Explorer |
| Right-click a tile icon | Pin / Unpin / reveal / copy path |
| Right-click the tile body | Rename, delete, slots, lock, shell toggle, new bucket |
| Drag the tile body | Moves the tile (unless locked); position is persisted |
| Drop files on the tile | Copies them into the bucket folder |

---

## Design decisions

### Scope: Fences-style overlay, not a shell extension

v1 draws each bucket as its own borderless top-level window. A true shell namespace /
icon-handler extension hooking `explorer.exe` is far more fragile across Windows updates
and is out of scope. The "New Bucket" desktop menu item is a plain
`HKCU\...\Directory\Background\shell` verb, not an entry inside the "New ▸" flyout (that
needs a `ShellNew` handler, which is awkward for folders).

### Open questions from the spec — resolved (all revisitable)

| Question | v1 decision | Rationale |
|---|---|---|
| "Recently worked" definition | `max(LastWriteTime, last-opened-via-tile)`. The tile stamps a timestamp when *it* launches a file. | Reliable, no NTFS last-access dependency, no global launch hook |
| Refresh behaviour | Live. `FileSystemWatcher` per folder, 250 ms debounce | Matches how Explorer feels |
| Pin limit / order | Unlimited pins; only the top *N* show; order = order pinned (oldest first), new pins appended | Predictable; an old reliable pin is never bumped off by a newer one |
| Missing / moved file | Dropped from the tile silently, next candidate promoted. A pinned-but-missing file stays in `.bucket.json` (so a briefly-offline path can reappear) and is pruned on next startup if still gone | No surprises, tolerant of network paths |
| Slot count | Default 4; 1–9 per bucket via the tile context menu | — |

### Storage

- `%APPDATA%\DesktopBuckets\buckets.json` — just the list of bucket folder paths.
- `<bucket folder>\.bucket.json` (hidden) — id, display name, pin list, last-opened map,
  slot count, window position, lock flag. Keeping state *in the folder* makes a bucket
  portable.
- `%APPDATA%\DesktopBuckets\log.txt` — best-effort rolling diagnostic log (512 KB cap).

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
Pushing a `v*` tag runs [`.github/workflows/release.yml`](.github/workflows/release.yml),
which does the above on a runner and publishes a GitHub Release with the installer attached.

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
    NativeMethods.cs         P/Invoke: SHGetFileInfo, SetWindowPos, SHFileOperation, …
    DesktopWindowHelper.cs   WS_EX_NOACTIVATE + hold-at-bottom Z-order
  Models/
    BucketConfig.cs          the .bucket.json shape
    BucketFile.cs            immutable snapshot of one file + activity key
    Bucket.cs                folder + config; enumerate / pin / rename / prune
  Services/
    BucketStore.cs           the buckets.json index
    BucketManager.cs         owns windows + watchers + tray; implements IBucketHost
    BucketWatcher.cs         debounced FileSystemWatcher
    FileRankingService.cs    pinned-first + recent-fill selection (pure, testable)
    IconService.cs           SHGetFileInfo → frozen ImageSource, cached
    ShellIntegration.cs      HKCU "New Bucket" verb
    SingleInstance.cs        mutex + named-pipe command forwarding
    RecycleBin.cs            SHFileOperation wrapper
    JsonUtil.cs / Log.cs
  ViewModels/
    BucketTileViewModel.cs   slots, grid dims, empty/overflow state, file actions
    BucketFileViewModel.cs   one slot
  Views/
    BucketTileWindow.xaml(.cs)   the tile
    InputDialog.xaml(.cs)        name prompt
    TrayIconController.cs        WinForms NotifyIcon
```

---

## Roadmap

Not yet implemented / rough edges:

- **First-class pin reordering** (drag pins within the tile; today pin order = the order
  you pinned them).
- **Auto-start with Windows** (Run key / Startup shortcut) — currently manual.
- **WorkerW wallpaper parenting** as an option, for tiles that should hide on "Show
  desktop" like real desktop icons. v1 keeps them visible (Fences-style).
- **Multi-monitor position clamping** on display-configuration changes is basic.
- **Folder drops** (dropping a directory onto a tile is currently ignored; files only).
- **Theming / size presets**, custom accent, compact vs comfortable density.
- **Bundled app icon** (`Resources\app.ico`) — the tray currently falls back to a system
  icon.
- **Tests** for `FileRankingService` and `Bucket` pin/prune logic (pure, easy to cover).
