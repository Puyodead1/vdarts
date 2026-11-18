# VShootingKernel for Windows

A clean-room C reimplementation of `libVShootingKernel.so` (the dartboard I/O driver,
build string "Jul 18 2019"). I reverse engineered it from the x86_64 ELF and the
`linux` class in `Assembly-CSharp.dll`.

## Install

Set `VDARTS_GAME_DIR` to the absolute path of the game folder (the one holding `VDartsH2_V1.exe`). Then run `build.bat`, which needs
Visual Studio with the C++ x64 tools. The script stops if the variable is missing or doesn't point at the game. It copies `VShootingKernel.dll`
next to `VDartsH2_V1.exe`. This Unity 2018.4 player does not load plugins from
`VDartsH2_V1_Data\Plugins`.

## Hardware link

The board is a USB serial device (Linux saw it as `/dev/ttyACM*` or `/dev/ttyUSB*`). It runs at 115200 8N1 in raw mode and sends one message per line.

| Board → PC line | Meaning | `VShootingGet()` |
|---|---|---|
| `A1`..`L8` | segment hit (table in source) | 1..82 |
| `M1`..`M5` | buttons | 83..87 |
| `%a<ch><value>` | analog sensor sample | ch0 → 0 (miss), ch1 → 100 (coin), ch2 → 101+n (bill pulses), ch4..8 → 90..94 |

The game sends 4-byte commands from PC to the board with `VShootingSet(delayMs, 4, b0..b3)`. These
commands drive the lamps and set the analog sensor gains. At start the DLL sends the same init sequence as the original.

## Choosing the COM port

1. Set the `VSHOOTING_PORT` environment variable, for example `COM5`, or
2. Put `VShootingKernel.ini` next to the DLL with the line `port=COM5`.
3. If neither is set, the DLL tries every port in `HKLM\HARDWARE\DEVICEMAP\SERIALCOMM` in order.

If no board is found, the game still runs. The DLL retries every 2 s, so you can plug the board in later.

## Board emulator (playing without the real board)

`build\emulator\VDartsBoardEmulator.exe` stands in for the dartboard controller. It needs the .NET 8 Desktop Runtime.
Start the game first, then the emulator. The status reads "connected" once the DLL is running.

* **Click the board to throw a dart.** You can hit outer and inner singles, trebles, doubles, the bull and the double bull. Each click sends the
  same `A1`..`L8` line the real board sends.
* **Cabinet buttons:** Up/Down/Left/Right/OK (`M1`..`M5`, codes 83–87). From the keyboard, use the arrows or WASD, and Enter for OK.
* **Insert coin** (`C`), **Bill pulses** (`B`) and **Miss** (`M`) send the same analog sensor waveforms the real hardware sends.
  The DLL's own threshold logic then turns them into coin, bill or miss events.
* **Connect via:** the named pipe is the default and needs no drivers. You can also choose a COM port. To exercise the real
  serial path, install a virtual null-modem pair such as com0com (e.g. COM10↔COM11). Set `VSHOOTING_PORT=COM10`
  for the game and select COM11 in the emulator. The log then also shows the lamp and config commands the game sends.

* **Cards:** this box shows the NFC reader's status and the last card read: its UID, type, any written number, and the numbers derived from the UID.
  * **Write to NFC card** writes the 10-digit number into the card's data area, then reads it back to verify. Use it to recreate a member card or GameKey.
  * **Tap virtual card** and **Operator card** send a number to the game through VDartsMod (UDP `127.0.0.1:47851`) without any card hardware.
  * **Send reader taps to game** forwards real taps from the emulator. Leave it off when VDartsMod is reading the same reader itself.

The emulator also has a scripting mode, used by the round-trip test:

```powershell
.\build\emulator\VDartsBoardEmulator.exe --send coin wait:800 20 60 bull dbull ok bill:10 miss
```

In `--send`, numbers are game codes: 1–20 outer single, 21–40 treble, 41–60 inner single, 61–80 double, 81 bull and 82 double bull.
Names such as `up`, `ok` and `coin` also work, as do `bill:N`, `wait:ms` and `card:0123456789`.

## BepInEx mod (VDartsMod)

BepInEx 5.4.23.2 (x64, Mono) is unpacked into the game folder: `winhttp.dll`, `doorstop_config.ini` and `BepInEx\`. Plugins go in
`BepInEx\plugins\`, logs go to `BepInEx\LogOutput.log` and settings to `BepInEx\config\`. `build.bat` builds
`VDartsMod\` (net472) and installs it as `BepInEx\plugins\VDartsMod.dll`. Settings are in `BepInEx\config\vdarts.mod.cfg`.

### Keyboard

| Key | Action | Game code |
|---|---|---|
| ↑ ↓ ← → | cabinet Up / Down / Left / Right | 83 / 84 / 85 / 86 |
| Enter, keypad Enter | cabinet OK | 87 |
| C | insert one credit | `Money.Insert(1)` |
| O | miss (dart off the board) | 0 |
| digits + I | debug dart: `1xx` single, `2xx` double, `3xx` triple, `4xx` inner single, `125` bull, `225` double bull (`\` / Delete clears) | 1–82 |
| F12 | tap the operator (GameKey) card | — |
| F11 | tap the test member card (`TestCardNumber`) | — |
| Esc | main menu: back, or Quit? on the top level | — |

Every key can be rebound in the config. The game ships a debug handler, `keyboard.cs`, that already used these keys plus
WASD. The plugin replaces that handler so a key press is never sent twice, and drops WASD. Other `Input.GetKey`
calls in the game are developer leftovers on scenes that a cabinet never loads (DailyRankingsSystem A/S/D/F, PictureSplit A/S,
UnitTest_UGUI R/S/P, VDartsPK Space).

### Operator menu

Main menu → **Member Zone** (the last item; internally `Setting`) → tap a GameKey card on that screen ("GameKey Card to modify Machine's Settings"). You can use a real card, the emulator's **Operator card** button, or F12.
`GameKey.Check` accepts:
* the staff cards built into the game: `2513081404` and `1762817609`. These work offline.
* the cabinet's own GameKey, saved locally under `SettingGameKey`. To set it, throw **D9, D4, D5, D3** (codes 69, 64, 65, 63) on the main menu.
  The "Set GameKey" page opens. Tap the new card, then press Left to close the page.
* keys the server confirms through `/is_gamekey`.

The operator menu (`NewMenu2D_Setting`) shows three tabs: BASIC (language, volume, bull value, switch time, miss/coin sensor
thresholds, card-to-coins), ADVANCED (dartboard type, LED, free play, QR, auto-handicap, brightness, inspection, return darts,
standby time, rating info, taboo games, member login, clear coins) and DETAILS. Left/Right switch tabs and OK confirms.

### NFC cards (member and GameKey)

All card screens read a **10-digit decimal number** typed through `Input.inputString`: member login, league, the operator key,
and the key-card test. The original reader was a keyboard-wedge reader, so a USB reader that types 10 digits still works with no setup.
For PC/SC readers such as the ACR122U, the plugin reads the card and passes its number to the game for one frame, as if it had been typed.

A card's number is chosen in this order:
1. A number written on the card by the emulator. The 16-byte record `VDC1` + 10 digits + checksum is stored in MIFARE Classic block 4 (key A
   `FFFFFFFFFFFF`) or in Ultralight/NTAG pages 4–7.
2. `BepInEx\config\VDartsMod.cards.txt`, which has one `UIDHEX=0123456789` entry per line.
3. The first 4 UID bytes as a decimal number (`UidByteOrder = BigEndian`/`LittleEndian`). The log prints both orders for every tap, so you can
   match them against the numbers printed on existing cards.

## Testing without hardware (raw pipe)

The Linux fifo `/dev/shm/VShootingKernel.fifo` is replaced by the named pipe
`\\.\pipe\VShootingKernel`. It accepts these lines:

* `d<N>`: queue event N directly (same as the original fifo), for example `d100` for a coin
* any other line: parsed as if the board sent it, for example `E7` for single 1

```powershell
$p = New-Object IO.Pipes.NamedPipeClientStream('.', 'VShootingKernel', 'Out'); $p.Connect(1000); $w = New-Object IO.StreamWriter($p); $w.WriteLine('d100'); $w.Flush(); $p.Dispose()
```

## Logging

The original logged to `/opt/vdarts/log/ttyACM.log` only when that file existed. This port logs
to `VShootingKernel.log` next to the DLL only when that file exists. You can also set
`VSHOOTING_LOG=<path>`.

## Differences from the original

* The background threads always start, even with no board, so the pipe works and hot-plugging works.
* `/opt/vdarts/vdarts-u3d-reset.sh` is not run. The original ran it when the board repeated `A3` more than 10 times. This port only logs the event.
* The `VShootingCam*` V4L2/JPEG camera functions are not implemented. The game never calls them.
