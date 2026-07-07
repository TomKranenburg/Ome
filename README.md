(CAUTION: Originally written entirely by ChatGPT-4o in 2024. Optimised, extended,
and re-documented by Claude Fable (Anthropic) in 2026 — see `CHANGELOG.md` for
everything that changed and `OPTIMIZATIONS.md` for the design rationale.)

<div align="center">
  <img src="ome-logo-large.png" alt="Logo">
</div>

# OME Documentation

## Overview

Ome is an ambient soundscape mixer. It loads audio files from a folder, loops
them perpetually, and lets you layer them into a mix: independent volume and
stereo pan per track, slow organic drift of level and pan so the soundscape
feels alive, ReplayGain loudness matching, and configs to save and recall
whole mixes with one click. The folder is watched live — drop sounds in or
take them away and the app follows along without a restart.

Every control shows a short description on mouse-over.

**Supported formats:** `.flac`, `.wav`, `.mp3`. FLAC decoding uses Windows'
built-in Media Foundation decoder (Windows 10 1607 or later).

> **Tip:** prefer FLAC or WAV for loops. MP3 carries encoder padding that
> creates a small audible gap at the loop point, whereas FLAC and WAV loop
> seamlessly — shorter files (up to roughly a minute and a half) are looped
> straight from memory, sample-accurately.

## Quick start

1. Put `Ome.exe` in a folder with your audio files, or put the files in an
   `Audio` subfolder next to it, and run it.
2. Press **Play** on a few tracks and balance them with their sliders.
3. Open the menu (**☰**) and **Save Config** to keep the mix. It appears as a
   tile at the top for one-click recall — right-click the tile to give it a
   cover image.

## User Interface

### Top bar

Left to right: two recent-config tiles, **Reset** (returns every track to
defaults), **Menu**, **Pause/Resume** (pauses all playing tracks in place;
they hold their positions), two more recent tiles, and beneath them the
**global volume** slider, which scales the whole mix at the output stage.

### Recent config tiles

The four most recently loaded or saved configs appear as tiles — newest
first, two on each side of the buttons. Click one to load that mix;
right-click for **Set image…** and **Clear image**. Tile backgrounds are
transparent, so cover images with alpha channels show the window through. A
config without a cover shows the placeholder tile with its name written on
it; empty slots show the placeholder dimmed. Hovering shows the config's full
path, and clicking a tile whose file has been deleted removes it from the
list.

Covers are remembered in the app settings rather than written into the
configs, and the last twelve configs are tracked while four are shown — so an
assigned image survives its config temporarily dropping off the visible
tiles.

### Track rows

Each audio file gets one row, left to right:

| Control | Meaning |
|---|---|
| `00:00:00` | Playback position within the current loop |
| `000` | Completed loop count |
| Name | Filename without extension (hover for the full path) |
| Gain label | The file's ReplayGain (see below); blank until read |
| **Play / Stop** | Toggles playback (shows **Resume** while globally paused) |
| Volume slider + number box | Track level, 0.000–1.000; the box is editable |
| First checkbox | **Fluctuate** — volume breathes below the set level while playing |
| Pan slider | Stereo balance, hard left to right; a notch marks centre |
| Second checkbox | **Pan wander** — pan drifts around its set position while playing |
| Round button | Resets this track to defaults: stopped, counters zeroed, drift off, volume 0.5, pan centred |

The window sizes itself to the loaded tracks. If no audio files are found, a
message names the exact folder that was scanned, so a wrong launch location
is obvious at a glance.

### The gain label

A background scan reads every file's ReplayGain tags shortly after launch,
without playing anything:

- **Green** `+x.x dB` — the file will be boosted
- **Red** `-x.x dB` — the file will be cut
- White `0.0 dB` — tagged at exactly reference level
- Grey `—` — checked, and no ReplayGain tags found
- Blank — not read yet

The value shown is the gain actually applied (after clipping prevention), so
the label always tells the truth. Whether it is *applied* is controlled by
the menu checkbox; the label is informational either way.

### Fluctuation and pan wandering

Your slider positions are the **peak level** and the **home position**. With
the checkboxes ticked and the track playing:

- Volume glides between random targets at 45–100 % of the set level, each
  glide taking 4–12 seconds, eased so there are no corners — aperiodic, never
  metronomic.
- Pan drifts within ±0.6 of the set position on lazier 6–16 second glides.

The sliders and the number box animate with the drift, so you can see it
working. Dragging a slider mid-drift simply sets a new peak or home;
unticking (or stopping) snaps back to your set values; typing in the number
box pauses its updates while it has focus; global pause freezes drift
mid-glide.

## The menu

**Load Config** and **Save Config** work with soundscape `.json` files (see
below). **Use ReplayGain** toggles loudness matching. **Exit Application**
closes Ome — nothing is saved automatically on exit.

## ReplayGain

When enabled, each file's tagged gain is applied on top of its volume slider:
track gain preferred, album gain as fallback, peak tags capping any boost so
a file cannot be pushed into clipping, and corrupt tags clamped to ±24 dB. A
file with missing or unreadable tags simply plays at unity — a bad tag can
never stop playback.

Tag formats (via TagLib#): FLAC Vorbis comments, MP3 ID3v2 and APEv2, and ID3
chunks in WAV.

The toggle applies live to playing tracks and is remembered automatically.
Note that ReplayGain's reference level is conservative, so enabling it
typically makes material *quieter* — that is the feature working; compensate
with the global volume if desired.

## Live folder watching

- **Added files** appear at their alphabetical position. Files still being
  copied are not added half-written — Ome waits until the file is fully
  readable, retrying for up to ~30 seconds (large files over a network or
  from a slow drive).
- **Deleted or renamed files** update the list accordingly. Deleting a
  playing track stops and removes it (Windows usually blocks deleting a
  large *streamed* playing file, since Ome holds it open; shorter tracks play
  from memory and delete freely).
- **Files overwritten in place** get their audio and ReplayGain re-read; a
  playing track keeps looping its in-memory copy seamlessly and refreshes on
  stop.
- Playing tracks are never interrupted by a rescan, and if the folder itself
  vanishes (a removable drive), the list clears gracefully.

## Command-Line Parameters

```
Ome.exe [ConfigFilePath] [SoundFolderPath] [flags]
```

Positional parameters (order matters):

1. **ConfigFilePath** — a `.json` config to load at startup. Loaded only;
   never rewritten automatically.
2. **SoundFolderPath** — the folder to load audio from. Because parameters
   are positional, a folder requires a config path in front of it. With
   neither, Ome uses its own folder, or an `Audio` subfolder if its own
   folder contains no audio.

Flags (any position):

| Flag | Effect |
|---|---|
| `--minimize` / `-m` | Start minimised |
| `--pause` / `-p` | Pause all tracks |
| `--resume` / `-r` | Resume all tracks |
| `--no-focus` / `-nf` | Don't steal focus when the window appears |

**Controlling a running instance:** Ome is single-instance. Launching it
again forwards the arguments to the running app and exits — so `Ome.exe -p`
pauses a running Ome and `Ome.exe -r` resumes it, which makes the flags
scriptable from shortcuts or hotkey tools. Passing a config or folder path to
a running instance loads it in place.

## Configuration files

Configs are **manual snapshots**: the only thing that ever writes one is the
menu's **Save Config** button. Tweak a mix, save it, and the file stays
byte-for-byte until you deliberately overwrite it. Saves are atomic (written
to a temporary file and swapped in), so an interrupted save cannot corrupt an
existing config. Loading restores window placement (clamped onto the visible
screen), global volume, and every track's volume, pan, drift flags, and play
state. Configs from older versions load fine — missing fields take defaults.

```json
{
  "Tracks": [
    {
      "FilePath": "E:\\Music\\Ome\\Audio\\Rain.flac",
      "IsPlaying": true,
      "Volume": 0.8,
      "Fluctuate": true,
      "Pan": -0.25,
      "PanWander": false
    }
  ],
  "Window": {
    "Width": 839,
    "Height": 600,
    "Left": 100,
    "Top": 100,
    "GlobalVolume": 0.5
  }
}
```

Track fields: `FilePath` (matches tracks when loading), `IsPlaying` (starts
the track on load), `Volume` (0–1), `Fluctuate`, `Pan` (-1 left … +1 right),
`PanWander`.

## App settings

Application-level preferences live separately in
`%AppData%\Ome\settings.json` and persist automatically: the ReplayGain
toggle, plus the recent-configs list and its tile images. Deleting that file
resets them without touching any soundscape configs.

## Under the hood (briefly)

All tracks feed a single mixer on one output device; short files are decoded
once into memory and looped gaplessly from RAM within a bounded budget, while
long files stream from disk. Loading, tag reading, and decoding happen on
worker threads, so the interface never blocks. Panning uses a
balance-with-unity-centre law: dead centre is bit-exact passthrough, and
panning only attenuates the far channel — it never boosts, so it cannot
introduce clipping. Full architecture notes: `OPTIMIZATIONS.md`; complete
record of changes: `CHANGELOG.md`.

## Requirements & building

- **Runtime:** Windows 10 version 1607 or later (for the built-in FLAC
  decoder), .NET 8 Desktop Runtime.
- **Building:** open `Ome.sln` in Visual Studio or run `dotnet build` on
  Windows. NuGet restores the two dependencies: `NAudio` 2.2.1 and
  `TagLibSharp` 2.3.0. The repo's image assets (including `blank.png`, the
  tile placeholder) are embedded as resources at build time.
