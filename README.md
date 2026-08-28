# Jellyfin Jimaku plugin

Finds Japanese subtitles for anime episodes on [jimaku.cc](https://jimaku.cc), **verifies each
candidate against the local file's actual timing**, corrects constant offsets and framerate drift,
and writes the result as an external `.jpn.ass` / `.jpn.srt` sidecar so it shows up in every
Jellyfin client, mobile included.

When it cannot establish confidence, it writes nothing and tells you why.

## Why the verification matters

A Jimaku entry for a single episode routinely holds a dozen files timed against different releases:
TV broadcast versus Blu-ray, different fansub groups, PAL-converted rips. Attaching the wrong one
gives subtitles that drift or sit seconds out, which is worse than having none — and it quietly
erodes trust in every other subtitle the plugin wrote.

So the filename is only ever used as a cheap pre-filter. The decision to attach is made on measured
timing:

1. **Reference.** Preferably the cue timings of a subtitle track already embedded in the media —
   fast, and language-agnostic, since an English or signs-only track marks the same moments in time.
   Failing that, voice activity detection over the Japanese audio.
2. **Alignment.** Both signals are reduced to 10 ms bins and cross-correlated by FFT over a ±60 s
   lag range, once for each of seven candidate framerate ratios.
3. **Confidence.** Two numbers gate the result: a baseline-corrected correlation coefficient, and a
   peak-to-second-peak ratio measuring how *unique* the best alignment is.
4. **Correction.** A pure shift, a linear rescale, per-section offsets for a differing cut, or a
   refusal.

### On the confidence metric

The obvious measure — normalized cross-correlation — turns out to be unusable here. For binary
signals it reduces to `overlap / sqrt(activeA · activeB)`, and two *unrelated* tracks each active a
fraction `p` of the time overlap by chance about `p` of the time. NCC therefore has a floor equal to
the duty cycle: dialogue-dense subtitle tracks score around **0.6 against a completely different
episode**, which is above any threshold you would want to set.

Subtracting the overlap expected under independence fixes it, leaving the Pearson correlation of the
two indicator sequences. Measured separation across 20 unrelated synthetic episode pairs:

| | correlation | uniqueness |
|---|---|---|
| Correct match (worst observed) | 1.00 | 1.51 |
| Wrong episode (best observed) | 0.28 | 1.03 |
| **Default thresholds** | **0.50** | **1.20** |

The real ffmpeg + VAD path recovers a known 4.2 s offset at correlation 0.94 and uniqueness 1.31 —
which is why the uniqueness default is 1.20 rather than something tighter.

## What it will and will not fix

| Situation | Result |
|---|---|
| Already in sync | Attached unchanged |
| Whole file shifted by a constant | Shifted; inline ASS tags untouched |
| PAL/NTSC framerate drift | Rescaled, including karaoke and animation tag timings |
| Different cut (TV subtitle on a Blu-ray) | Split into sections with separate offsets |
| Subtitle for a different episode | **Declined**, with an explanation |
| No embedded track and unusable audio | **Declined** |

Styling is preserved byte for byte. The rewriter only ever replaces the two timecodes on
`Dialogue:` and `Comment:` lines; `[Script Info]`, `[V4+ Styles]`, `[Aegisub Project Garbage]`,
embedded fonts and inline override tags pass through untouched. For a shift-only correction the
output is byte-identical to the input apart from the timecodes.

## Requirements

- Jellyfin **10.11.x** (the plugin targets `net9.0`; 10.10 is `net8.0` and is not supported)
- A Jimaku API key from <https://jimaku.cc/account>
- ffmpeg, which the server already provides

## Building

Needs the **.NET 9 SDK**.

```bash
dotnet build -c Release
dotnet test
```

## Installing

### From the plugin repository (recommended)

In Jellyfin: **Dashboard → Plugins → Repositories → +**, and add

```
https://raw.githubusercontent.com/mcgrizzz/jimaku-jellyfin/main/dist/manifest.json
```

Then **Catalogue → Subtitles → Jimaku → Install**, and restart the server. Updates from then on are
a single click in the dashboard.

### By hand

```bash
# On the Jellyfin server
mkdir -p /var/lib/jellyfin/plugins/Jimaku_1.0.0.0
cp src/Jellyfin.Plugin.Jimaku/bin/Release/net9.0/Jellyfin.Plugin.Jimaku.dll \
   src/Jellyfin.Plugin.Jimaku/bin/Release/net9.0/AnitomySharp.dll \
   /var/lib/jellyfin/plugins/Jimaku_1.0.0.0/
systemctl restart jellyfin
```

Both DLLs are required. Then open **Dashboard → Plugins → Jimaku** and set your API key; the
settings page has a *Test key* button.

If the plugin shows as `NotSupported`, the server is not 10.11.x.

### Publishing a new version

```bash
pip install --user jprm
scripts/release.sh 1.0.1.0     # tests, packages, updates dist/manifest.json
git add dist && git commit -m "Release 1.0.1.0" && git push
```

Jellyfin picks it up on its next repository refresh.

## Using it

**Per episode.** The plugin registers as a Jellyfin subtitle provider, so the normal *Search
subtitles* action on any episode lists Jimaku's files with their filename match score. Picking one
downloads it, verifies its timing, corrects it, and attaches it.

**From the settings page.** Search for an episode and either *Fetch best* (fully automatic) or
*Show candidates* to review and choose. This is also where declines explain themselves.

**Scheduled.** Enable the sweep in settings; it runs daily over episodes with no Japanese subtitle
track. It is deliberately more conservative than interactive use — differing-cut correction is off
by default, because nobody is watching. Restrict it to your anime libraries; by default it covers
everything.

Episodes are recorded in a small history file so repeat sweeps skip settled items, and declines are
retried after a configurable interval since Jimaku gains uploads over time.

## Optional: Silero voice activity detection

For episodes with **no embedded subtitle track**, timing has to come from the audio, and anime is
the hardest case: the mix is scored almost continuously, so energy-based detection has very little
to work with. The built-in detector uses speech-band energy plus a spectral flatness test, which
helps but is clearly weaker than a trained model.

Silero is supported but not bundled — ONNX Runtime carries ~185 MB of native binaries across all
platforms, which is an unreasonable payload for a fallback path. To enable it:

1. Build `src/Jellyfin.Plugin.Jimaku.Silero` and copy `Jellyfin.Plugin.Jimaku.Silero.dll` into the
   plugin folder.
2. Copy `Microsoft.ML.OnnxRuntime.dll` plus the native library for your platform
   (`libonnxruntime.so` / `onnxruntime.dll`) alongside it.
3. Download `silero_vad.onnx` and set its path in the plugin settings.

Anything missing or unloadable falls back silently to the built-in detector.

## Prior art and credits

- [`bpwhelan/Emby.Jimaku`](https://github.com/bpwhelan/Emby.Jimaku) — the Emby plugin that
  established the Jimaku-on-a-media-server idea. It is Emby-only and does no candidate selection.
- [Bazarr's Jimaku provider](https://github.com/morpheus65535/bazarr/blob/master/custom_libs/subliminal_patch/providers/jimaku.py)
  — the candidate filtering rules (archives, machine transcriptions, size floors) follow it.
- [ffsubsync](https://github.com/smacke/ffsubsync) and [alass](https://github.com/kaegi/alass) — the
  alignment approach. The FFT correlation with a framerate grid is ffsubsync's; the split-penalty
  dynamic program for differing cuts is alass's idea by way of ffsubsync's `split_aligner`.
  Both are reimplemented here in C# rather than shelled out to.
- [Kometa Anime-IDs](https://github.com/Kometa-Team/Anime-IDs) — TVDB and AniDB to AniList mapping.
