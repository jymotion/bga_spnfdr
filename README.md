# bga_spnfdr

Displays route notes for **BattleTanx: Global Assault** (N64) speedruns based
on the player's current score, read live from the capture feed with OCR.

## Using it

1. **Select Capture Window**, then click the window showing the N64 feed
   (e.g. the OBS projector or media player).
2. The program attempts to **auto-detect the game feed** within the window by
   watching for movement for a few seconds. If it fails to accurately crop
   the game feed, you can click **Re-try Automatic Crop** to try again or
   manually crop it yourself.
4. **Select Route CSV** — column 1 = score, column 2 = note text (quoted
   multi-line notes work). When the confirmed score matches a CSV row, its
   note appears in the notes window — and stays up until a different score
   matches (a stale note beats a vanished one mid-run).
5. **Change Notes Formatting** opens a dialog for font family, size,
   bold/italic, text color, background color, and always-on-top. Changes
   preview live on the actual notes window; Save keeps them, Cancel reverts.
   The window is automatically sized to fit the largest note in the CSV.

During a run the player only needs the notes window: once configured,
**close the setup window** — capture and OCR keep running. Right-click (or
double-click) the notes window for "Capture setup…" and "Exit". The notes
window is always visible — it IS the program — and closing it exits the app.

Everything — capture window (by title), crop, CSV path, formatting,
notes-window position, setup-window size — persists across launches in
`%APPDATA%\bga_spnfdr\settings.txt`. The status bar at the bottom of the
setup window shows live OCR activity (current score, rejected misreads,
holding through menus).

## Building

Requires the .NET 8 SDK (build machine only):

```
dotnet build bga_spnfdr.csproj -c Release
```

Output: `bin\Release\net48\bga_spnfdr.exe`. Ship that single file (the
`.pdb` and `.exe.config` are not needed).

## OCR safeguards

All BattleTanx-specific knowledge is baked into the code (constants at the
top of `MainForm.cs`, font mappings in `ScoreParser.cs`, image pipeline in
`ScoreImage.cs`). The displayed score only changes when a read survives all
of these checks, in order:

1. **Lookalike mapping** — Windows OCR has no digits-only mode and reads the
   game font's digits as letters (5→S/s/t, 0→O/U/Ü, 9→g/Y/y/J, 1→I/l, 2→Z,
   7→r, 8→B); all mapped back (`ScoreParser.cs`).
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
   junk-prefix reads like "773550" from 273850).
9. **Hold on loss** — when the score is hidden (menus, cutscenes, explosions)
   the last confirmed value is kept rather than cleared.
