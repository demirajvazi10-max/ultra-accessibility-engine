# Ultra Creative Suite

**Professional creative tools, fully accessible to blind and sighted users — without compromise.**  
Built by a blind developer. Tested daily with JAWS for Windows.

> "I am blind and I use JAWS for Windows. I built this because no professional creative software on the market is actually usable with a screen reader." — Author

---

## Demo

This video was created entirely by the author — who is blind — using Ultra Creative Suite with JAWS for Windows. No sighted assistance.

[![Ultra Creative Suite Demo](https://img.youtube.com/vi/K1mXPN4hEFs/maxresdefault.jpg)](https://www.youtube.com/watch?v=K1mXPN4hEFs)

> A children's song video: lyrics analyzed by AI, stock footage automatically selected and downloaded, mood-based color grading applied, ambient sounds mixed, rendered to 4K. Created independently by a blind user.

---

## What is this?

Ultra Creative Suite is a collection of professional creative tools for Windows, built from the ground up with full accessibility as a core requirement — not an afterthought.

Every feature works for blind, low-vision, and sighted users equally. No stripped-down "accessible mode". No features hidden behind visual-only interfaces. Full keyboard control. Full JAWS and NVDA compatibility.

---

## Tools

### 🎬 Ultra Video Editor

Professional non-linear video editor with AI-powered automation.

- Native Win32 ListView timeline — JAWS reads every clip natively, identical to File Explorer
- AI video generation from a single audio file: lyrics → stock footage → color grading → subtitles → 4K render
- AI transcription via faster-whisper (local, GPU-accelerated, Serbian language support)
- NVENC GPU rendering via FFmpeg, automatic CPU fallback
- Ken Burns effect, transitions, text overlays, audio ducking
- Undo/redo, keyframes, markers, export profiles (YouTube, TikTok, Instagram)

→ [VideoEditor source](./VideoEditor/)

---

### 🎵 Ultra Audio Editor

Professional multi-track audio editor with live DSP effects.

- Multi-track timeline, import/export WAV MP3 OGG FLAC M4A AIFF
- Live DSP effects (no re-render needed): EQ, Reverb, Delay, Compressor, Noise Gate, Bass Boost, Pitch Shift, Chorus
- Win32 native context menu — Shift+F10 opens instantly, JAWS reads every item
- Vocal/instrumental separation via Demucs (Meta AI, runs locally, free, no data uploaded)
- AI transcription and analysis via Groq (free) or Anthropic Claude
- Accessible effect dialogs — each effect has its own window with labeled sliders and text inputs
- Playhead slider fully keyboard-controlled
- Mixdown / combine tracks to file

→ [AudioEditor source](./AudioEditor/)

---

## Accessibility — How it works

Both tools share the same accessibility philosophy:

**Win32 native controls where it matters most.** The timeline and file lists use the same Windows controls as File Explorer. JAWS reads them without plugins, workarounds, or custom accessibility code.

**No mouse required.** Every feature — import, edit, export, effects, AI functions — is reachable by keyboard alone.

**Context menus that work.** Shift+F10 or the Apps key opens a native Win32 context menu. JAWS announces every item. No WPF popup menus that screen readers silently ignore.

**Live regions for status.** Playhead position, render progress, and action confirmations are announced automatically as ARIA assertive regions.

**Accessible dialogs.** Every dialog uses standard controls with proper labels and logical tab order.

---

## Requirements

| Component | Requirement |
|-----------|-------------|
| Windows | 10 or 11, 64-bit |
| .NET Runtime | 8.0 |
| JAWS for Windows | Any recent version (optional) |
| FFmpeg | Required for Video Editor |
| Python 3.8+ | Optional, for Demucs vocal separation |

**.NET 8 Runtime:** https://dotnet.microsoft.com/download/dotnet/8.0

---

## Quick Start

### Video Editor
```cmd
git clone https://github.com/demirajvazi10-max/Ultra-Creative-suite.git
cd Ultra-Creative-suite/VideoEditor
dotnet run --project UltraVideoEditor.csproj
```

### Audio Editor
```cmd
cd Ultra-Creative-suite/AudioEditor/UltraAudioEditor
dotnet run --project UltraAudioEditor.csproj
```

---

## AI Features

| Provider | Cost | Used for |
|----------|------|----------|
| **Groq** | Free | Transcription (Whisper), analysis, EQ recommendations |
| **Ollama** | Free, local | Story generation, scene descriptions (Video Editor) |
| **Anthropic Claude** | Pay per use | Advanced analysis |
| **Demucs** | Free, local | Vocal/instrumental separation (Audio Editor) |
| **faster-whisper** | Free, local | GPU-accelerated transcription (Video Editor) |

---

## Hardware Tested On

- **CPU:** Intel Core i9-10885H
- **GPU:** NVIDIA RTX 2060 Max-Q (NVENC)
- **RAM:** 32GB DDR4 3200MHz
- **Output:** 4K H.264 via NVENC

---

## Current Status

Both tools are **fully functional** and actively used in real-world production by the author.

**Current language:** Serbian (ekavica dialect). English localization is planned. Contributions welcome.

---

## NLnet / NGI

This project is being developed as part of an application to the [NLnet Foundation](https://nlnet.nl) NGI0 Commons Fund.

The goal: blind and visually impaired users should have access to the same professional creative tools as sighted users — not simplified alternatives, not workarounds, but the real thing.

---

## Support the Project

If Ultra Creative Suite is useful to you, or you believe accessible creative tools matter, consider supporting development:

[![Support on Ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/ultracreativesuite)

Every contribution helps keep this project alive and growing.

---

## Contributing

Issues and pull requests welcome.

Accessibility bugs — anything JAWS or NVDA does not read correctly — are treated as critical and fixed first.

See [CONTRIBUTING.md](./CONTRIBUTING.md) for guidelines.

---

## License

GPL-3.0 — see [LICENSE](./LICENSE)

---

*Ultra Creative Suite — Because creativity has no boundaries.*
