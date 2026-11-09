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
   KDE Plasma and GNOME).
2. The program attempts to **auto-detect the game feed** within the window by
   watching for movement for a few seconds. If it fails to accurately crop
   the game feed, you can click **Re-try Automatic Crop** to try again or
   manually crop it yourself.
3. **Select Route CSV** — column 1 = score, column 2 = note text (quoted
   multi-line notes work). When the confirmed score matches a CSV row, its
   note appears in the notes window — and stays up until a different score
   matches (a stale note beats a vanished one mid-run).
4. **Change Notes Formatting** opens a dialog for font family, size,
   bold/italic, text color, background color, and always-on-top. Changes
   preview live on the actual notes window; Save keeps them, Cancel reverts.
   The window is automatically sized to fit the largest note in the CSV.

During a run the player only needs the notes window: once configured,
**close the setup window** — capture and OCR keep running. Right-click (or
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
Framework 4.8, WinForms and the OCR engine are part of Windows 10/11.

### Linux

Requires the .NET 8 SDK and clang (build machine only; on Arch:
`sudo pacman -S dotnet-sdk clang`):

```
dotnet publish Linux/bga_spnfdr.csproj -c Release -o out
```

Output: `out/bga_spnfdr`, one self-contained native program (about 2 MB,
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
| `Core/` | Platform-neutral logic: BattleTanx constants, OCR safeguards, route CSV loading | Every build |
| `Windows/` | Windows app: window capture, Windows OCR, WinForms UI | Windows build only |
| `Linux/` | Linux app: screen-sharing portal + PipeWire capture, built-in digit reader (no OCR engine), GTK 3 UI | Linux build only |
| `Tests/` | Tests for `Core/` (`dotnet test Tests`) | Nothing shipped |
| `Tools/ReaderBench/` | Measures the digit reader on a recorded run against hand-verified ground truth | Nothing shipped |

Each app's project
compiles only its own folder plus `Core/`, straight into its executable (no
shared DLL), so nothing from one platform can end up in the other's build.
`Core/` code must build on both, so it can't use Windows- or Linux-only
APIs (the test project, which targets plain .NET 8, catches that).

## Reading the score

**Windows** uses the OCR engine built into Windows. **Linux** has no
built-in OCR, so it uses `Linux/DigitReader.cs` instead: it matches the
game's own digit shapes (2.5 KB of templates), finds the score wherever it
sits under the minimap (its position changes per level), copes with the
proportional font, and searches a couple of pixel rows up and down because
being one row off loses digits. On a full recorded run (`Tools/ReaderBench`)
it confirmed every score shown for 2+ frames with none wrong, using under 1%
of one CPU core at 30 reads a second.

## OCR safeguards

All BattleTanx-specific knowledge is baked into the code (constants in
`Core/BattleTanx.cs`, checks below in `Core/ScoreTracker.cs`, font mappings in
`Core/ScoreParser.cs`, Windows image pipeline in `Windows/ScoreOcr.cs`).
The displayed score only changes when a read survives all of these checks, in
order (steps 1, 2, 6 and 7 are Windows OCR specific; the Linux reader only
outputs digits from one image per frame):

1. **Lookalike mapping** — Windows OCR has no digits-only mode and reads the
   game font's digits as letters (5→S/s/t, 0→O/U/Ü, 9→g/Y/y/J, 1→I/l, 2→Z,
   7→r, 8→B); all mapped back (`Core/ScoreParser.cs`).
2. **Unmappable characters reject the whole read** — dropping a bad character
   would silently shift the number (e.g. "47987S" → 47987 instead of 479875).
3. **Length cap** — BattleTanx scores never reach 7 digits, so longer reads
   are garbage with extra characters.
4. **Score increments** — scores are always multiples of 25; any other value
   is a misread.
5. **No leading zeros** — the score is never zero-padded, so a read like
   "03850" is a fragment of a longer number, not a real 3850.
6. **Two OCR variants per frame** — a binarized image (6x upscale → threshold
   at median-luminance + margin, digits are the distinctly-bright pixels →
   3x3 dilation to thicken the thin game font → white padding) and the plain
   color image; each reads frames the other misses. If both produce
   *different* valid numbers, the frame is skipped as ambiguous. (The upscale
   must happen before dilation with enough headroom: at 4x the dilation
   filled the loops of the 8, and 273850 read as 273550.)
7. **Scale retry** — the engine is flaky about scale on this font; if the
   binarized image yields nothing valid it is retried at 0.5x and 1.5x.
8. **Consecutive-read confirmation** — a new value must be read identically
   on 2 consecutive frames; a *lower* value than the current score, or a
   forward jump over 50,000, needs 5 (scores only go up mid-run and never
   leap — such reads are fragments like "25" off the end of 468025, or
   junk-prefix reads like "773550" from 273850). The Linux build reads every
   frame (~30 a second) instead of 10, so it needs 3 and 8 to cover the same
   span of time; back-to-back frames look almost identical, so a misread can
   repeat. Measured on a full recorded run with `Tools/ReaderBench`.
9. **Hold on loss** — when the score is hidden (menus, cutscenes, explosions)
   the last confirmed value is kept rather than cleared.
