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
2. The program **finds the game picture** in the window by itself. Do this
   on the game's **GAME SETUP menu**: the program recognises that screen
   and puts the red box in the preview exactly on the game picture, in
   under a second. The window may hold much more than the game (a whole
   stream layout with a webcam, or OBS's own window), and the picture may
   be stretched (4:3 shown as 16:9).
   Anywhere else in the game it goes by what moves, which other moving
   things in the window can fool; the box then corrects itself as soon as
   the menu or a score is on screen. **Re-try Automatic Crop** starts over.
   There is no cropping by hand: a hand-drawn box would have to be right
   to within a few pixel rows.
   The box is labelled *from the game's menu* or *not confirmed yet* until
   the first score of a run (25 or more) appears. The program then checks
   the crop against the score, corrects it if need be, and remembers it as
   *confirmed by the score* for later launches. If the captured window
   later changes size, the crop no longer fits, so the program finds the
   game picture again (and doesn't read the score until it has).
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

Output: `out/bga_spnfdr`, one self-contained native program (about 1.5 MB,
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
| `Core/` | Platform-neutral logic: the digit reader, score band sampling, finding the game picture, safeguards, BattleTanx constants, route CSV loading | Every build |
| `Windows/` | Windows app: window capture, WinForms UI | Windows build only |
| `Linux/` | Linux app: screen-sharing portal + PipeWire capture, GTK 3 UI | Linux build only |
| `Tests/` | Tests for `Core/` (`dotnet test Tests`) | Nothing shipped |
| `Tools/ReaderBench/` | Measures the digit reader on a recorded run against hand-verified ground truth, and how well the game picture is found in different kinds of capture (`layouts`) | Nothing shipped |

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
app falls back to PrintWindow at 10 reads a second. Windows only sends a
new picture when the window changes; a read that would see the same picture
again is skipped (every third in a row still happens, so a score confirms
on a paused game too).

The Linux app asks the desktop for at most 30 frames a second (GNOME and
KDE honor that) and copies only the score strip out of each.

## Finding the game picture

The reader needs to know where the game picture is in what's captured: to
within about 5 rows where the score is, 4% in height, and from 4% narrower
to 12% wider in width (measured on the reference run; beyond that, misread
scores get confirmed). Setup usually happens on the game's menu, where
there is no score and nothing in the game moves, while a webcam beside it
or the capturing program's own meters do. So `Core/GameFinder.cs` goes by
three things, each specific to the game where it can be:

1. **The GAME SETUP menu** (`Core/MenuFinder.cs`). The menu's backdrop is
   always the same picture, so a small copy of it (48 x 36 brightness
   values, 864 bytes, with the part where the menu's text goes left out) is
   looked for at every size, shape and place in the frame, the way the
   reader looks for digits. Where it matches is where the game picture is,
   to a pixel or two. It takes about 50 ms, and is tried as soon as a
   window is picked and every few seconds until a score has confirmed the
   crop.
2. **What moves**, when the menu isn't showing: the largest moving region,
   grown to the picture's own edge (the black around it, or the outermost
   straight edge where the surroundings aren't black). This is only a
   first guess: a webcam can be the largest thing that moves.
3. **The score** (`Core/ScoreFinder.cs`), once one is on screen. It is
   looked for with the reader's own digit shapes: about once a second
   where the crop says it should be, and otherwise through the whole
   frame at different sizes, in small pieces (about 12 ms per tenth of a
   second, and nothing while the picture is still). Only the game has a
   BattleTanx score in it. A find has to repeat on later frames. A crop
   that already fits the score (as one from the menu does) is kept as it
   is. Otherwise the digits fix where the score band is; how far apart
   they are fixes the width (the font's spacing is known to 0.15 px); the
   picture's top edge, 423 reference rows above them, fixes the height;
   and the left edge is where, going left from the score, the picture
   stops moving and nothing was ever lit (or a straight edge stays put).
   The crop is then saved as confirmed, and none of this runs again.

`ReaderBench -- layouts` plays the reference run inside different kinds of
capture and checks the crop against the truth:

| Capture | At setup | Once the score shows |
|---|---|---|
| Set up on the menu: as captured, on a 1080p projector, stretched to 16:9, in a stream layout (webcam, animation, timer), in the capturing program's own window (webcam and still image beside the game, meters moving) | all 5 recognised by the menu in 0.7 s, within a pixel or two | confirmed 0.4 to 1.0 s after the score has 2 digits; the crop stays as it was |
| Set up during play: the same kinds, 13 cases | right in all 13, from what moves | confirmed within about a second of the 4 s of watching |
| Starting from a wrong crop during play: a little off, 7% too small, half the picture, a box in the middle of it, the whole frame or window (11 crops) | | all 11 corrected: within a second when roughly right, about 4 s more when the whole 1080p frame has to be searched |
| No game in the frame (webcam, animation, timer) | | never confirmed (60 s) |

A tester's capture of OBS's own window on the GAME SETUP menu (the game
beside two webcams) was recognised to within a pixel as well.

If a level puts the score at the very edge of the picture and the crop's
left edge is a little too far right, the score is read starting in the
margin the reader keeps there (24 reference pixels), and the crop is moved
left.

## Safeguards

The displayed score only changes when a read survives all of these checks
(`Core/ScoreTracker.cs`, constants in `Core/BattleTanx.cs`):

1. **Plausible number** — digits only, at most 6 (scores never reach
   1,000,000), a multiple of 25 (scores always move in 25s), and no leading
   zero (the score is never zero-padded, so "039950" is 239950 with a lost
   digit).
2. **Consecutive-read confirmation** — a new value must be read identically
   on 2 consecutive reads; a *lower* value than the current score, or a
   forward jump of 100,000 or more, needs 5 (scores only go up mid-run;
   such reads are fragments with lost leading digits, like "25" off the end
   of 468025, or have a stray digit in front. Level-end bonuses are real
   jumps of up to 64,100 on the reference run). Both apps normally read every frame (~30 a second)
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
