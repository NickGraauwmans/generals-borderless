# Generals Borderless

A fix for the stutter and hitching in **C&C Generals** and **Zero Hour** fullscreen on Windows 11, including **ShockWave** and other mods.

Since about September 2026, exclusive fullscreen in these games stutters and hitches on Windows 11. Windowed mode runs smoothly, but it has a title bar and the taskbar covers the bottom of the game. This patch runs the game windowed but without a frame, laid exactly over your monitor: it looks like fullscreen and runs as smoothly as windowed mode.

The patch is installed once. After that the game always runs this way, however it is started: the ShockWave launcher (LAUNCH, QUICKSTART, with or without WINDOWED), a shortcut, GenPatcher's launcher, other mods. No game data is changed, so LAN games still work with players who don't have the patch.

Even without the hitching it is a handy way to play: borderless fullscreen, where alt-tab and a second monitor work like with any other window, and the mouse stays inside the game while it has focus, so edge scrolling keeps working.

## Install

1. Download `GeneralsBorderless.zip` from [Releases](../../releases/latest) and unzip it.
2. Run `GeneralsBorderless.exe` and allow the admin prompt (game folders are in Program Files).
3. It lists the game folders it found (Zero Hour, Generals). Leave them ticked and click **Install**.
   For a mod with its own copy of the game in another folder, click **Add folder...** and pick that folder.
4. Close it and play the way you always do.

Windows SmartScreen may warn because the exe isn't signed: **More info > Run anyway**. Some antivirus programs distrust a `dinput8.dll` they don't know; allow it if yours removes it.

## Remove

Run `GeneralsBorderless.exe` again, tick the folders and click **Remove**. Everything is put back.

## Update

Download the new release, run its `GeneralsBorderless.exe` and click **Install**. Folders with the old version show "Installed, older version".

## Settings

`GeneralsBorderless.ini` in the game folder:

| Setting | Effect |
| --- | --- |
| `Enabled=0` | Turn the patch off without removing it: the game starts as before. |
| `ForceNativeResolution=0` | Keep the resolution picked in the game's options. A lower one is centred with the desktop around it (not stretched). |
| `LockCursor=0` | Don't keep the mouse inside the game. |

## How it works

Install puts two files in each game folder:

- `dinput8.dll`: the game loads it by itself. It hands everything on to Windows' own `dinput8.dll` (DirectInput) and, inside the game:
  - adds `-win` to the command line, so the game runs windowed instead of in exclusive fullscreen, which is what hitches;
  - creates the game window without a frame and keeps it centred on its monitor, which at the monitor's resolution means covering it exactly;
  - sets the resolution in the game's `Options.ini` to the monitor's before the game reads it;
  - makes the game DPI aware, so Windows display scaling (125%/150%) doesn't blow it up;
  - keeps the mouse inside the game while it has focus.

  If the folder already had a `dinput8.dll` from something else, that one is kept as `dinput8_original.dll` and still used; Remove puts it back.
- `GeneralsBorderless.ini`: the settings above.

If the patch stops working after GenPatcher replaced files, run `GeneralsBorderless.exe` and click **Install** again. WorldBuilder is not affected. Online services (GenTool online, GameRanger) have not been tested; if one complains, set `Enabled=0` or click **Remove**.

Logs: `%LOCALAPPDATA%\GeneralsBorderless\game.log` (the game) and `patcher.log` (install/remove).

The patcher can also be scripted: `GeneralsBorderless.exe -install | -remove | -status [-path <game folder>]...`
