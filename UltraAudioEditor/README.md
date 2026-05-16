# 🎵 Ultra Audio Editor

**Professional Windows audio editor with full screen reader accessibility.**  
Built from the ground up so blind and sighted users have equal access to every feature.

> Part of the **Ultra** platform — an open accessible engine for professional creative tools.

---

## Why This Exists

Most audio editors are graphically intensive and practically unusable with a screen reader.
Ultra Audio Editor is different: every single function is reachable by keyboard, every element is announced correctly by JAWS for Windows, and nothing requires a mouse.

At the same time, it is a fully functional audio editor — not a simplified version of one.

---

## Features

### Audio Editing
- Multi-track timeline with unlimited tracks
- Import WAV, MP3, OGG, FLAC, M4A, AIFF
- Export to WAV, MP3, OGG, FLAC, M4A, AIFF
- Clip positioning to millisecond precision
- Mixdown / combine tracks into one file
- Undo / Redo

### Live DSP Effects (real-time, no re-render needed)
- **Equalizer** — 3-band (bass 200 Hz, mid 1 kHz, treble 8 kHz), ±12 dB
- **Reverb** — mix and room size
- **Delay / Echo** — delay time and feedback
- **Compressor** — threshold, ratio, attack, release
- **Noise Gate** — threshold
- **Bass Boost** — up to 24 dB gain
- **Pitch Shift** — ±12 semitones
- **Chorus** — depth

All effects update in real time while audio is playing.

### AI Features
- **Vocal / Instrumental separation** — powered by [Demucs](https://github.com/facebookresearch/demucs) (Meta AI, free, runs locally, no data leaves your machine)
- **Transcription** — via Groq Whisper API (free)
- **Noise removal analysis**, **EQ recommendations**, **LUFS loudness analysis** — via Groq (free) or Anthropic Claude

### Accessibility
- **JAWS mode** — dedicated accessible view: track list + file list, nothing else on screen
- **Playhead slider** — keyboard-controlled: arrow keys (0.1s steps), Ctrl+arrows (1s), Home/End
- **Context menus** — Shift+F10 or Apps key, every item fully announced by JAWS
- **Effect dialogs** — each effect has its own dialog with labeled sliders and text input fields; no custom WPF controls embedded in menus
- **Live ARIA regions** — playhead position announced as it changes
- Complete Tab order; mouse is never required for any function

---

## Requirements

| Component | Requirement |
|-----------|-------------|
| Windows | 10 or 11, 64-bit |
| .NET Runtime | 8.0 |
| JAWS for Windows | Any recent version (optional) |
| Python | 3.8+ (optional, only for Demucs vocal separation) |

**.NET 8 Runtime:** https://dotnet.microsoft.com/download/dotnet/8.0

---

## Getting Started

### Run from source

```cmd
git clone https://github.com/your-username/UltraAudioEditor.git
cd UltraAudioEditor
dotnet run --project UltraAudioEditor/UltraAudioEditor.csproj
```

### Build a standalone .exe (no .NET required on target)

```cmd
dotnet publish UltraAudioEditor/UltraAudioEditor.csproj -c Release -r win-x64 --self-contained -o publish
```

### Open in Visual Studio 2022

Open `UltraAudioEditor.sln` and press F5.

---

## Keyboard Reference

### Transport

| Key | Action |
|-----|--------|
| Space | Play / Pause |
| S | Stop |
| Home | Go to beginning |
| End | Go to end |
| L | Toggle loop |

### File and Project

| Key | Action |
|-----|--------|
| Ctrl+N | New project |
| Ctrl+O | Open project |
| Ctrl+S | Save |
| Ctrl+Shift+S | Save as |
| Ctrl+I | Import audio |
| Ctrl+E | Export / mixdown |

### Accessibility

| Key | Action |
|-----|--------|
| Alt+W | Switch JAWS mode / Visual mode |
| F6 | Read project status aloud |
| Shift+F10 | Context menu for focused track or clip |
| Apps | Context menu (alternative key) |
| F2 | Set clip position (opens dialog) |
| Tab / Shift+Tab | Navigate between elements |

### Clips

| Key | Action |
|-----|--------|
| Ctrl+→ | Move clip right 1s |
| Ctrl+← | Move clip left 1s |
| Ctrl+Shift+→ | Move clip right 0.1s |
| Ctrl+Shift+← | Move clip left 0.1s |
| Delete | Delete selected clip |

### Playhead slider (when focused)

| Key | Action |
|-----|--------|
| → / ← | Move 0.1 seconds |
| Ctrl+→ / ← | Move 1 second |
| Home | Go to beginning |
| End | Go to end |
| Space | Play / Pause |
| Enter or Tab | Move focus to track list |

---

## JAWS Mode

Press **Alt+W** to switch to JAWS mode. The interface becomes a focused two-panel layout.

**Left panel — Track list**  
Each track announces: name, type, volume, number of files, total duration, mute/solo state.  
Press Shift+F10 for the track context menu.

**Top — Playhead panel**  
Slider always visible. Time display updates live as ARIA assertive region.

**Right panel — File list**  
Native ListView with columns: Name · Start (MM:SS) · Start (s) · Duration (MM:SS) · Duration (s) · End (MM:SS).  
Press Shift+F10 for the clip context menu.

### Track context menu

- Import audio / Import at playhead position / Import at custom position
- **Vocal and Instrumental separation** (Demucs, local AI)
- Mute / Solo toggle
- Volume dialog / Pan dialog
- **Effects submenu** — each effect shows current state (on/off), opens dedicated parameter dialog
- Normalize / Fade In / Fade Out
- Combine with another track (mixdown to file)
- Move up / down / Duplicate / Rename / Delete

### Effect dialogs

Each effect opens its own dialog window containing:
- Checkbox: enable / disable
- Slider + text input for each parameter (fully keyboard editable)
- Reset to defaults

The dialog is non-blocking — playback continues so you can hear changes live.

---

## Vocal / Instrumental Separation

Uses [Demucs](https://github.com/facebookresearch/demucs) by Meta AI Research. Runs entirely locally — no audio is uploaded anywhere.

**Install once:**

```cmd
pip install demucs
```

Then: track context menu → **Vocal and Instrumental separation...**

- **2 stems** — Vocals + Instrumental (faster, ~2–5 min per song)
- **4 stems** — Vocals + Drums + Bass + Other (more detailed)

Separated stems are imported automatically as new tracks.

---

## AI Features Setup

### Groq — Free

1. Register at https://console.groq.com
2. API Keys → Create API Key
3. In the app: right panel → AI Functions → Groq → enter key

Provides free access to Llama 3 (analysis) and Whisper (transcription).

### Anthropic Claude — Pay per use

1. Register at https://console.anthropic.com
2. API Keys → Create Key
3. In the app: AI Functions → Anthropic → enter key

---

## Project Structure

```
UltraAudioEditor/
├── Models/
│   └── AudioModels.cs          — AudioProject, AudioTrack, AudioClip, TrackEffects
├── ViewModels/
│   └── MainViewModel.cs        — all commands, state management, playback control
├── Views/
│   ├── MainWindow.xaml/.cs     — main window, dual mode switching
│   ├── Controls/
│   │   └── AccessibleTrackList — JAWS mode: track list + file list + playhead
│   ├── EffectDialog.cs         — accessible per-effect parameter dialogs
│   ├── SetClipPositionDialog   — clip position input (seconds or MM:SS)
│   └── SetValueDialog.cs       — generic labeled value input dialog
├── Services/
│   ├── AudioEngine.cs          — NAudio playback, live DSP chain, export/mixdown
│   ├── DemucsService.cs        — Demucs subprocess wrapper, stem import
│   └── AnthropicService.cs     — Groq and Anthropic Claude API client
└── Controls/
    └── WaveformControl.cs      — waveform renderer (visual mode only)
```

**Live DSP chain** (per clip, evaluated in real time):
```
File → Mono/Stereo → Resample → NoiseGate → Compressor → EQ → BassBoost → Chorus → Delay → Reverb → Volume → Offset → Mixer
```

---

## NLnet / NGI

This project is part of the **Ultra** platform — a proposal for fully accessible professional creative tools.  
The goal is that blind and visually impaired users should not have to use stripped-down alternatives when professional tools exist.

If you are from NLnet or NGI0, welcome. Feel free to open an issue or contact us.

---

## License

MIT — free to use, modify, and distribute.

---

## Contributing

Pull requests welcome. If you use a screen reader and find anything that JAWS or NVDA does not announce correctly, please open an issue — accessibility regressions are treated as critical bugs.
