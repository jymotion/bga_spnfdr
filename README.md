# bga_spnfdr

Displays route notes for **BattleTanx: Global Assault** (N64) speedruns based
on the player's current score, read live from the capture feed. Runs on
Windows and Linux.

## Using it

1. **Select Capture Window**, then pick the window showing the N64 feed
   (e.g. the OBS projector or media player). On Windows, click it. On Linux,
   your desktop's screen-sharing prompt opens (the same one OBS and browsers
   use); pick the window there. Linux remembers the choice, so later
   launches reconnect without asking (where the desktop supports it, e.g.
   KDE Plasma and GNOME). On Windows 10, Windows draws a yellow border
   around the window while it's being captured (Windows 11 lets the app
   turn it off).
2. The program attempts to **auto-detect the game feed** within the window by
   watching for movement for a few seconds. If it fails to accurately crop
   the game feed, you can click **Re-try Automatic Crop** to try again or
   manually crop it yourself.
3. **Select Route CSV** — column 1 = score, column 2 = note text (quoted
   multi-line notes work, blank lines included; a one-line note may hold
   commas without quotes). When the confirmed score matches a CSV row, its
   note appears in the notes window — and stays up until a different score
   matches (a stale note beats a vanished one mid-run).
4. **Change Notes Formatting** opens a dialog for font family, size,
   bold/italic, text color, background color, and always-on-top. Changes
   preview live on the actual notes window; Save keeps them, Cancel reverts.
   The window is automatically sized to fit the largest note in the CSV.

During a run the player only needs the notes window: once configured,
**close the setup window** — capture and reading keep running. Right-click (or
double-click) the notes window for "Capture setup…" and "Exit". The notes
window is always visible — it IS the program — and closing it exits the app.

Everything — capture window, crop, CSV path, formatting, notes-window
position, setup-window size — persists across launches in
`%APPDATA%\bga_spnfdr\settings.txt` (Windows) or
`~/.config/bga_spnfdr/settings.txt` (Linux). The status bar at the bottom of
the setup window shows live reading activity (current score, rejected
misreads, holding through menus).

## Building

### Windows

Requires the .NET 8 SDK (build machine only):

```
dotnet build Windows\bga_spnfdr.csproj -c Release
```

Output: `Windows\bin\Release\net48\bga_spnfdr.exe`. Ship that single file
(the `.pdb` and `.exe.config` are not needed). It needs nothing else: .NET
Framework 4.8 and WinForms are part of Windows 10/11.

### Linux

Requires the .NET 8 SDK and clang (build machine only; on Arch:
`sudo pacman -S dotnet-sdk clang`):

```
dotnet publish Linux/bga_spnfdr.csproj -c Release -o out
```

Output: `out/bga_spnfdr`, one self-contained native program (about 1.7 MB,
under 1 MB compressed; no .NET install needed to run it). Ship that single
file (`bga_spnfdr.dbg` is debug symbols). It uses what Linux desktops
already have: GTK 3, PipeWire, and the screen-sharing portal
(xdg-desktop-portal plus the desktop's backend, e.g.
xdg-desktop-portal-kde). It works on Wayland and X11. On Wayland it runs
through XWayland, because only there may a window keep itself above other
windows and remember its position, which the notes window needs.

For testing without a desktop's prompt, `BGA_SPNFDR_PIPEWIRE_NODE=<id>`
makes it read straight from a node on your own PipeWire (e.g. a GStreamer
`pipewiresink` playing a recording).

## Repository layout

| Folder | What's in it | Compiled into |
|---|---|---|
| `Core/` | Platform-neutral logic: the digit reader, score band sampling, safeguards, BattleTanx constants, route CSV loading | Every build |
| `Windows/` | Windows app: window capture, WinForms UI | Windows build only |
| `Linux/` | Linux app: screen-sharing portal + PipeWire capture, GTK 3 UI | Linux build only |
| `Tests/` | Tests for `Core/` (`dotnet test Tests`) | Nothing shipped |
| `Tools/ReaderBench/` | Measures the digit reader on a recorded run against hand-verified ground truth | Nothing shipped |

Each app's project
compiles only its own folder plus `Core/`, straight into its executable (no
shared DLL), so nothing from one platform can end up in the other's build.
`Core/` code must build on both, so it can't use Windows- or Linux-only
APIs (the test project, which targets plain .NET 8, catches that).

## Reading the score

Both builds read the score with `Core/DigitReader.cs`, which matches the
game's own digit shapes (1.3 KB of templates) instead of using an OCR
engine. It finds the score wherever it sits under the minimap (the position
changes per level), copes with the proportional font, and searches 5 pixel rows up and
down, because being one row off loses digits and a crop is easily a few
rows off (the run reads the same with the crop moved up to 5 rows either
way; `ReaderBench`'s misalignment argument checks it).
`Core/ScoreBand.cs` cuts the score band out of the captured frame and scales
it, so any capture resolution works (checked from 480x360 to 1440x1080).

Measured on a full recorded run against a hand-verified answer key
(`Tools/ReaderBench`, 111 distinct scores):

| | Scores confirmed | Wrong | Time per read |
|---|---|---|---|
| Windows and Linux (30 reads/s) | 109 | 0 | ~0.4 ms |
| Windows on its PrintWindow fallback (10 reads/s) | 103 | 0 | ~0.4 ms |

The scores it misses were on screen for under ~0.2 s. Run side by side on
Windows, it beat the Windows OCR this app used before (104 vs 89 scores,
none wrong either way, ~1 ms vs 45 ms per read).

The Windows app captures with Windows.Graphics.Capture (Windows 10 version
1903 and later): Windows keeps the window's picture on the graphics card,
and each read copies back only the score strip, so reading 30 times a
second stays cheap whatever the window's size. On a test PC it took
0.36 ms per read against 2.6 ms for PrintWindow, and used less CPU in total
than PrintWindow at 30 reads a second. On older Windows, or if it fails, the
app falls back to PrintWindow at 10 reads a second.

## Safeguards

The displayed score only changes when a read survives all of these checks
(`Core/ScoreTracker.cs`, constants in `Core/BattleTanx.cs`):

1. **Plausible number** — digits only, at most 6 (scores never reach
   1,000,000), a multiple of 25 (scores always move in 25s), and no leading
   zero (the score is never zero-padded, so "039950" is 239950 with a lost
   digit).
2. **Consecutive-read confirmation** — a new value must be read identically
   on 2 consecutive reads; a *lower* value than the current score, or a
   forward jump over 50,000, needs 5 (scores only go up mid-run and never
   leap; such reads are fragments with lost leading digits, like "25" off
   the end of 468025). Both apps normally read every frame (~30 a second)
   instead of 10, so they need 3 and 8 to cover the same span of time;
   back-to-back frames look almost identical, so a misread can repeat.
   (On its 10-a-second fallback the Windows app uses 2 and 5.)
3. **No fragments** — a value *lower* than the current score is dropped
   when the reader sees a faint digit just in front of it: it is the end
   of the real score, whose leading digit fell just under the reader's
   threshold. At some capture sizes that can last long enough to pass
   check 2 (at 704x528 and 896x672 the reference run confirmed "39800" off
   239800 before this check; `ReaderBench`'s scale argument, 1.1 and 1.4).
4. **Hold on loss** — when the score is hidden (menus, cutscenes,
   explosions) the last confirmed value is kept rather than cleared, and a
   note stays up until a different score has one.
