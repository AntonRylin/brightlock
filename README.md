# BrightLock

Tray app for monitor brightness over DDC/CI (replacement for Monitorian).

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
