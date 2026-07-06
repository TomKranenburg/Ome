(CAUTION: Originally written entirely by ChatGPT-4o in 2024. Optimised, extended,
and re-documented by Claude Fable 5 (Anthropic) in 2026 — see `CHANGELOG.md` for the full
list of changes and `OPTIMIZATIONS.md` for the design rationale.)

<div align="center">
  <img src="ome-logo-large.png" alt="Logo">
</div>

# OME Documentation

## Overview

Ome is an ambient soundscape mixer. It loads audio files from a folder, loops
them perpetually, and lets you layer them into a mix: independent volume and
stereo pan per track, slow organic drift of level and pan if you want the
soundscape to feel alive, ReplayGain-based loudness matching, and configs to
save and recall whole mixes. The folder is watched live, so dropping new
sounds in (or removing them) updates the app without a restart.

**Supported formats:** `.flac`, `.wav`, `.mp3`. FLAC decoding uses Windows'
built-in Media Foundation decoder (Windows 10 1607 or later).

> **Tip:** prefer FLAC or WAV for loops. MP3 files carry encoder padding that
> produces a small audible gap at the loop point; FLAC and WAV loop
> seamlessly (and small files are looped from memory, sample-accurately).

## Features

1. **Dynamic audio loading** from the executable's folder or an `Audio`
   subfolder, with **live watching**: files added, removed, or renamed while
   the app runs appear/disappear automatically, in alphabetical order.
2. **Per-track playback controls**: play/stop, volume slider with an exact
   numeric box, stereo pan with a centre notch, playback position and loop
   counters.
3. **Organic drift**: per-track checkboxes make the volume slowly "breathe"
   (glides between random levels below your set peak) and the pan slowly
   wander around its set position. The sliders animate with the drift so you
   can see it working.
4. **ReplayGain**: an optional loudness match using each file's ReplayGain
   tags, with the detected gain displayed next to every track.
5. **Global controls**: pause/resume everything, reset everything, and a
   master volume applied once at the output stage.
6. **Per-track reset**: a round button on each row returns that track to its
   defaults (stopped, counters zeroed, drift off, volume 0.5, pan centred).
7. **Configuration snapshots**: save and load complete mixes — play states,
   volumes, pans, drift flags, window placement — as `.json` files. Saving is
   always manual; the app never rewrites a config on its own.
8. **Command-line control** of a running instance: Ome is single-instance,
   and a second launch forwards its arguments to the running app.

## User Interface

### Track rows

Each audio file gets one row, left to right:

| Control | Meaning |
|---|---|
| `00:00:00` | Playback position within the current loop pass |
| `000` | Completed loop count |
| Name | Filename without extension |
| Gain label | The file's ReplayGain (see below); blank until read |
| **Play / Stop** | Toggles playback (shows **Resume** while globally paused) |
| Volume slider + number box | Track level, 0.000–1.000; the box is editable |
| First checkbox | **Fluctuate**: volume breathes below the set level while playing |
| Pan slider | Stereo balance, hard left to hard right, notch at centre |
| Second checkbox | **Pan wander**: pan drifts around its set position while playing |
| Round button | Resets this track to defaults |

The window sizes itself to the loaded tracks. If no audio files are found, a
message names the exact folder that was scanned, so a wrong launch location
is obvious at a glance.

### The gain label

Once a file's ReplayGain tags have been read (a background scan fills the
column shortly after launch, without playing anything):

- **Green** `+x.x dB` — the file will be boosted
- **Red** `-x.x dB` — the file will be cut
- White `0.0 dB` — tagged at exactly reference level
- Grey `—` — the file was checked and carries no ReplayGain tags
- Blank — not read yet

The value shown is the gain actually applied (after clipping prevention), so
the label always tells the truth. Whether the gain is *applied* is controlled
by the menu checkbox; the label is informational either way.

### Volume fluctuation and pan wandering

Your slider positions are treated as the **peak level** and the **home
position**. With the checkboxes ticked and the track playing:

- Volume glides between random targets at 45–100 % of the set level, each
  glide taking 4–12 seconds, eased so there are no corners — aperiodic, never
  metronomic.
- Pan drifts within ±0.6 of the set position on lazier 6–16 second glides.

The sliders and the number box animate with the drift. Dragging a slider
mid-drift simply sets a new peak/home; unticking (or stopping) snaps
everything back to your set values. Global pause freezes drift mid-glide.

### Global controls

- **Pause/Resume** pauses all playing tracks in place (they hold position and
  resume exactly where they were).
- **Reset** returns every track to defaults.
- **Global volume** scales the whole mix at the output stage.
- **Menu (☰)** opens the configuration window: **Load Config**, **Save
  Config**, the **Use ReplayGain** toggle, and **Exit Application**.

## ReplayGain

When **Use ReplayGain** is ticked in the menu, each file's tagged gain is
applied on top of its volume slider: track gain is preferred, album gain is
the fallback, and peak tags cap any boost so a file cannot be pushed into
clipping. Corrupt tags are clamped to ±24 dB, and a file with unreadable tags
simply plays at unity — a bad tag can never stop playback.

Tag formats supported (via TagLib#): FLAC Vorbis comments, MP3 ID3v2 and
APEv2, and ID3 chunks in WAV.

The toggle applies live to playing tracks and persists automatically in
`%AppData%\Ome\settings.json` (application settings are separate from
soundscape configs and need no save step). Note that ReplayGain's reference
level is conservative, so enabling it typically makes material *quieter* —
that is the feature working; compensate with the global volume if desired.

## Live folder watching

The sound folder is monitored while the app runs:

- **Added files** appear at their alphabetical position. Files still being
  copied are not added half-written — Ome waits until the file is fully
  readable (retrying for up to ~30 seconds, which covers large files arriving
  over a network or from a slow drive).
- **Deleted or renamed files** update the list accordingly. Deleting a
  playing track stops and removes it. (Windows usually blocks deleting a
  large *streamed* playing file, since Ome holds it open; smaller tracks play
  from memory, so those delete freely and simply stop.)
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

1. **ConfigFilePath** — a `.json` config to load at startup. It is loaded
   only; Ome never writes back to it automatically.
2. **SoundFolderPath** — the folder to load audio from. Because the
   parameters are positional, specifying a folder requires a config path in
   front of it. With neither, Ome uses its own folder, or an `Audio`
   subfolder if its own folder contains no audio.

Flags (any position):

| Flag | Effect |
|---|---|
| `--minimize` / `-m` | Start minimised |
| `--pause` / `-p` | Pause all tracks |
| `--resume` / `-r` | Resume all tracks |
| `--no-focus` / `-nf` | Don't steal focus when the window appears |

**Controlling a running instance:** Ome is single-instance. Launching it
again forwards the arguments to the running app and exits — so, for example,
`Ome.exe -p` pauses a running Ome and `Ome.exe -r` resumes it, which makes
the flags scriptable from shortcuts or hotkey tools. Passing a config or
folder path to a running instance loads it in place.

## Configuration Files

Configs are **manual snapshots**: the only thing that ever writes one is the
menu's **Save Config** button. Tweak a mix, save it, and the file stays
byte-for-byte until you deliberately overwrite it. Saves are atomic (written
to a temporary file and swapped in), so an interrupted save cannot corrupt an
existing config.

Loading a config restores window placement (clamped onto the visible
screen), global volume, and every track's volume, pan, drift flags, and play
state. Configs from older versions of Ome load fine — missing fields take
their defaults.

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
    },
    {
      "FilePath": "E:\\Music\\Ome\\Audio\\Fire.flac",
      "IsPlaying": false,
      "Volume": 0.5,
      "Fluctuate": false,
      "Pan": 0,
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

Track fields: `FilePath` (full path, used to match tracks when loading),
`IsPlaying` (whether the track starts on load), `Volume` (0–1), `Fluctuate`
(volume breathing on/off), `Pan` (-1 left … +1 right), `PanWander` (pan drift
on/off). Window fields include `GlobalVolume` alongside size and position.

## Under the hood (briefly)

All tracks feed a single mixer on one output device; short files are decoded
once into memory and looped gaplessly from RAM within a bounded budget, while
long files stream from disk. Loading, tag reading, and decoding happen on
worker threads, so the interface never blocks. Panning uses a
balance-with-unity-centre law: dead centre is bit-exact passthrough, and
panning only attenuates the far channel — it never boosts, so it cannot
introduce clipping. The full architecture notes live in `OPTIMIZATIONS.md`,
and everything that changed from the original version is recorded in
`CHANGELOG.md`.

## Requirements & Building

- **Runtime:** Windows 10 version 1607 or later (for the built-in FLAC
  decoder), .NET 8 Desktop Runtime.
- **Building:** open `Ome.sln` in Visual Studio or run `dotnet build` on
  Windows. NuGet restores the two dependencies: `NAudio` 2.2.1 and
  `TagLibSharp` 2.3.0.
