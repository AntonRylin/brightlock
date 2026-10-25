# BrightLock

Tray app for monitor brightness over DDC/CI (replacement for Monitorian).

## Download

**[Download BrightLock-Setup.exe](https://github.com/AntonRylin/brightlock/releases/latest/download/BrightLock-Setup.exe)** (Windows 10/11, about 80 KB).

Run it and click OK. It installs for your Windows account only (no admin rights), starts with Windows and puts a sun icon in the taskbar tray (it may be under the ^ arrow). Running a newer setup later updates it and keeps your settings. Uninstall from Settings > Apps > Installed apps.

The files are not code-signed, so Windows SmartScreen may say "Windows protected your PC": click **More info**, then **Run anyway**. Prefer no installer? Download `BrightLock.exe` from the [latest release](https://github.com/AntonRylin/brightlock/releases/latest) and run it from any folder.

Your monitors need DDC/CI turned on (usually on by default, otherwise in the monitor's own menu). Laptop screens are not supported yet.

## Features

- Click the tray sun: iOS-style brightness pills, one per monitor. Drag, click, scroll (Ctrl = 1% steps) or arrow keys.
- Lock (on by default): every 2 s it reads each monitor and sets the brightness back if something else changed it. Changes are written to `%APPDATA%\BrightLock\log.txt`.
- Link button: move all monitors together.
- HDR button under each monitor: turns Windows HDR on/off for that screen (only shown when the monitor supports HDR).
- Power button under each monitor: Windows stops using that screen (like Win+P 'show only on' the other one) and it sleeps; the greyed pill or 'Turn on' brings it back with your normal extended layout.
- Moon button: warm screen filter (less blue light, about 3500 K), separate from Windows Night light.
- Tray menu: put all screens to sleep now (mouse/keyboard wakes them).
- Starts with Windows (tray menu to turn off).

Build: `.\build.ps1` (uses the C# compiler that ships with Windows). `.\build.ps1 -Install` copies to `%LOCALAPPDATA%\Programs\BrightLock` and starts it.
Settings: `%APPDATA%\BrightLock\settings.txt` (`poll` = seconds between checks).

License: MIT (see LICENSE).
