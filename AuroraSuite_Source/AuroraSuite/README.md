# Aurora Suite

A Windows tool for Aurora (Xbox 360 custom dashboard) users that combines two
things into one app:

1. **Image Assets** - converts your own images (png/jpg/bmp) into the
   `.asset` files Aurora reads for game art (icon, boxart, background,
   screenshots).
2. **Sync** - uploads those `.asset` files to your console over **FTP or
   XBDM**, whichever you pick.

Themed to match XeVator (Forest Green).

**XBDM needs no password.** Aurora's FTP server can be configured to demand a
username/password; the Xbox debug protocol (XBDM, TCP port 730) has no login
step at all. If you'd rather not deal with FTP credentials, enable xbdm in
DashLaunch and set "Sync using" to XBDM.

There's also a **README tab built into the app itself** that explains all of
this in plain language, so you don't have to come back to this file once
it's running.

## Features

- **Image Assets tab**: click "Scan Source" to detect titles into a grid (same
  Include/Select All/Deselect All/Status/Detail pattern as the Sync tab -
  nothing converts until you click "Convert Selected"). Point Source at
  either one Title ID folder or a parent folder full of many - both are
  auto-detected, no toggle needed.
- Per title folder, each asset type (Icon / Boxart / Background /
  Screenshots) is detected independently:
  - a named subfolder (`Icon\`, `Coverart\`, `Background\`, `Screenshots\`)
    always wins if it exists,
  - otherwise a recognizably-named loose image is used (`icon.png`,
    `boxart.png`, `background.png`, `screenshot1.png`, ...),
  - and if a title folder has just one unrecognized image with nothing else
    found, that image is used as the Coverart (covers a plain
    `Covers\TitleID\xxxx.png` dump).
- **Screenshots are combined**: every image in a `Screenshots\` folder (or
  every `screenshot*`/`ss*` file loose in the folder) gets packed into one
  `SS<TitleID>.asset`, up to 20 images - the only type that works this way.
- Conversion is done with real, hardcoded C# calling the actual
  `AuroraAsset.dll` (the same DLL AuroraAssetEditor uses) via a small native
  bridge - no Python/PIL dependency at runtime.
- **Sync tab**: click "Load Library" to scan your local library folder into a
  grid (one row per Title ID, showing which asset files it actually has),
  tick which rows to include (or Select All / Deselect All), then "Sync
  Selected" uploads only what's ticked. Status and a per-row Detail/Error
  column update live as the sync runs - color-coded like XenonArchivist's
  own review grid. Sync type checkboxes (Icon/Boxart/Background/Screenshots)
  above the grid are a second filter on top of Include, an "only overwrite
  files that already exist on the console" safety switch, and an **Apply
  All** button that ticks everything and syncs in one click.
- Console Connection/Paths/Folders sections are collapsible on both tabs, and
  both logs have a "Save Log to .txt" button that writes a timestamped file
  next to the exe.
- **Two transports.** A "Sync using" dropdown chooses FTP or XBDM; the sync
  engine itself is transport-agnostic, so both do the same job.
  - **FTP** - the original path, via Aurora's FTP server. Username/password
    are only sent if the server asks for them, so they're optional here too.
  - **XBDM** - talks the debug protocol (RDCP) straight down a TCP socket on
    port 730. **No username, no password - XBDM has no authentication.**
    Needs xbdm enabled in DashLaunch, and nothing installed on the PC.
  - Each transport has its own Test Connection button, and each runs the same
    round trip the sync does (connect, resolve the GameData path, list the
    title folders), so a passing test means syncing will work.
  - The GameData path can be typed in either style -
    `/Hdd1/Aurora_0.7b/Data/GameData` or `Hdd:\Aurora_0.7b\Data\GameData`.
    XBDM reads the console's real drive list and converts as needed.

## Output layout

Every asset type is written as its own file, never merged:

```
Output\
  4B4B0002\
    GL4B4B0002.asset   (Icon)
    GC4B4B0002.asset   (Boxart/Coverart)
    BK4B4B0002.asset   (Background)
    SS4B4B0002.asset   (Screenshots, can hold up to 20 images)
```

This is also the exact layout the Sync tab's "Local library folder" expects,
so you can point Sync straight at the Image Assets tab's Output folder.

## Project layout

```
AuroraSuite/
  lib/XDevkit.dll                     <- no longer used (kept for reference only)
  AuroraSuite/
    AuroraSuite.csproj
    App.xaml / App.xaml.cs
    MainWindow.xaml / MainWindow.xaml.cs
    Themes/Palettes.xaml               <- Forest Green accent colors
    Themes/Controls.xaml               <- shared control styles (buttons, tabs, inputs...)
    Assets/bridge.exe, AuroraAsset.dll, msvcr100.dll   <- embedded, extracted to %LocalAppData% at runtime
    Services/
      Settings.cs                      <- persisted to %AppData%\AuroraSuite\settings.json
      MiniFtpClient.cs                 <- minimal FTP client
      XbdmClient.cs                    <- raw-socket XBDM (RDCP) client, port 730, no auth
      ConsoleTransport.cs              <- IConsoleTransport + FTP/XBDM implementations + path translation
      SyncEngine.cs                    <- matches local Title ID folders to console folders and uploads
      AssetBridge.cs                   <- calls bridge.exe directly to run AuroraAsset.dll
      AssetContainerBuilder.cs         <- packs one or more encoded textures into a .asset file
      ImageProcessing.cs               <- resize/crop + raw pixel conversion
      ImageAssetConverter.cs           <- folder detection + conversion orchestration
```

## Build

Requires the .NET 8 SDK on Windows. `bridge.exe`, `AuroraAsset.dll` and
`msvcr100.dll` are 32-bit binaries, so the project targets
`PlatformTarget=x86`:

```
cd AuroraSuite
dotnet publish -c Release -r win-x86 --self-contained true -p:PublishSingleFile=true
```

## Known gaps

- Folder-name matching for `Icon`/`Coverart`/`Background`/`Screenshots` is
  case-insensitive and ignores spaces, and also accepts `Boxart` as an alias
  for `Coverart`. Open an issue if you use different naming.
- XBDM no longer goes through `XDevkit.dll` / COM. That old approach needed
  the Xbox 360 SDK installed and the console registered in Neighborhood as a
  real devkit, which is why it never answered on RGH/JTAG consoles. The
  protocol is now spoken directly over a socket, so there is nothing to
  install and no COM involved - and it works as a real transfer path, not
  just a connectivity check.
- XBDM uploads with `sendfile`, which is a whole-file operation: each upload
  rewrites the file from the start. Size is verified afterwards by re-reading
  the directory listing rather than `getfileattributes`, which reports 0 on
  some setups.
