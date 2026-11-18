# VDarts H2 preservation

These are tools to run the VDarts H2 arcade dart game (Unity 2018.4, Linux build) on Windows, mod it, and replace its online services.
The game itself is not in this repository.

| Folder | What it is |
|---|---|
| [`VShootingKernel-win/`](VShootingKernel-win/README.md) | Windows reimplementation of `libVShootingKernel.so` (the dartboard I/O driver). It also holds the board emulator (with NFC card read/write) and the `VDartsMod` BepInEx plugin (keyboard cabinet buttons, operator card, PC/SC NFC cards). |
| [`vdarts-server/`](vdarts-server/README.md) | Self-hostable replacement for the vdarts.net servers: member API, rankings, mall, ads, the online lobby (TCP 8008), and an admin page. |
| `mods/VDartsServerRedirect/` | BepInEx plugin that points the game's server addresses at `vdarts-server`. |

## Build

The plugins compile against the game's `VDartsH2_V1_Data\Managed\` DLLs and BepInEx, and the builds install into the game folder.
Set `VDARTS_GAME_DIR` to that folder's absolute path. Builds stop with an error if it is missing, relative, or not the game.
Unpack BepInEx 5.x x64 into the game folder first.

```powershell
$env:VDARTS_GAME_DIR = 'D:\Games\VDartsH2'
```

* `VShootingKernel-win\build.bat` builds the kernel DLL (MSVC x64), the emulator (.NET 8), and VDartsMod, then installs them into the game folder.
* `dotnet build mods\VDartsServerRedirect -c Release` builds the redirect plugin and copies it into `BepInEx\plugins\VDartsServerRedirect\`.
* `dotnet publish vdarts-server -c Release -r win-x64 -o <output folder>` builds the server. The server does not need the game.
