# QuickSnip

A Print Screen snipping tool for Windows 10/11 that works like the built-in Snipping Tool, but can run **as admin**, so Print Screen also works while an admin window is focused.

It's a single small `.exe` built with the C# compiler that ships with Windows. No installer, no downloads, no internet access.

## Features

**Snipping** (Print Screen opens the snip bar)
- Rectangle, window, full screen and freeform snips
- Text extractor: copies the text in an area (uses Windows' built-in OCR)
- Color picker: click any pixel to copy its color code (`#RRGGBB`)
- Magnifier lens with crosshair, cursor coordinates and selection size
- Pixel-exact crosshair cursor and selection box
- Choose the save folder right from the snip bar
- Snips go straight to the clipboard and are auto-saved (optional), with no popups
- Delayed snips (3, 5 or 10 seconds) and an optional border

**Markup window** (tray menu → *Mark up last snip*)
- Pen, highlighter (with colors and sizes), eraser
- Shapes: line, arrow, rectangle, oval
- Crop, zoom, undo/redo
- Copy all text, and *Quick redact* to black out e-mail addresses and phone numbers
- Copy, save as PNG/JPEG/GIF, print, open with, share

## Keyboard shortcuts

| Shortcut | Action |
|---|---|
| Print Screen | Snip bar |
| Win+Shift+S | Snip bar (can be turned off in Settings) |
| Ctrl+Print Screen | Text extractor |
| Shift+Print Screen | Instant full-screen snip (monitor under the mouse) |
| Ctrl+Shift+Print Screen | Instant snip of the active window |
| Esc / right-click | Cancel a snip |
| M | Magnifier on/off (while snipping) |

Markup window: `P` pen, `H` highlighter, `E` eraser, `S` shapes, `C` crop, `Ctrl+Z`/`Ctrl+Y` undo/redo, `Ctrl+C` copy, `Ctrl+Shift+C` copy all text, `Ctrl+S` save, `Ctrl+P` print, `Ctrl+N` new snip, `Ctrl+Plus`/`Ctrl+Minus`/`Ctrl+0` zoom.

Alt+Print Screen and Win+Print Screen keep Windows' own behavior.

## Download

Get `QuickSnip.exe` from the [Releases](../../releases) page and run it. A blue icon appears in the system tray.

To make Print Screen work over admin windows, right-click the tray icon → **Start with Windows as admin**. This creates a scheduled task that starts QuickSnip with admin rights when you sign in (no UAC prompt each time). Click it again to remove the task.

If Windows shows "Windows protected your PC" for a downloaded copy, click **More info → Run anyway**, or right-click the file → Properties → **Unblock**.

## Build it yourself

Run `build.bat`. It uses `csc.exe` from .NET Framework 4.x (included with Windows) and the WinRT metadata in `C:\Windows\System32\WinMetadata` for text recognition and sharing, and writes `QuickSnip.exe` next to the source.

`build.bat <path>` writes the exe somewhere else, for example `build.bat QuickSnip.new.exe`.

`QuickSnip.exe --selftest <folder>` runs offscreen checks (drawing, selection, magnifier, OCR, redaction, crop) on a generated test image and writes `results.txt` plus preview images to that folder. It never touches the real screen or clipboard.

## Settings and files

- Settings: `%APPDATA%\QuickSnip\settings.txt`
- Snips: `Pictures\Screenshots` by default (changeable from the snip bar)

## Requirements

Windows 10 or 11. Text recognition uses the OCR languages installed in Windows (English is usually there by default).
