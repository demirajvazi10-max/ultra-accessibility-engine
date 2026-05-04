# Ultra Creative Suite

**The world's first professional video editor that is fully accessible to blind, low-vision, and sighted users — without compromise.**

Built by a blind developer. Tested daily with JAWS for Windows.

---

## What is this?

Ultra Creative Suite is a professional video editor for Windows, built from the ground up with full accessibility as a core requirement — not an afterthought.

Every feature works for blind, low-vision, and sighted users equally. Blind users can independently create professional-quality videos, edit timelines, apply AI effects, transcribe audio, and render 4K output — without sighted assistance, and without a stripped-down "accessible mode" that hides features.

> "I am blind and I use JAWS for Windows. I built this because no professional video editor on the market is actually usable with a screen reader."
> — Author

---

## Demo

This video was created entirely by the author — who is blind — using Ultra Creative Suite with JAWS for Windows. No sighted assistance.

[![Ultra Creative Suite Demo](https://img.youtube.com/vi/K1mXPN4hEFs/maxresdefault.jpg)](https://www.youtube.com/watch?v=K1mXPN4hEFs)

> A children's song video: lyrics analyzed by AI, stock footage automatically selected and downloaded, mood-based color grading applied, ambient sounds mixed, rendered to 4K. Created independently by a blind user.

---

## Key Accessibility Features

**Native Win32 ListView timeline** — Uses the same Windows control as File Explorer. JAWS and NVDA read every clip natively without plugins or workarounds: clip name, type, duration, position, and AI-generated audio description.

**Live region status bar** — Every action, render progress, error, and confirmation is announced automatically. No need to manually navigate to find what happened.

**Full keyboard control** — Every feature is reachable without a mouse. No drag-and-drop required for any core workflow.

**AI audio descriptions** — Every image and video clip on the timeline receives an AI-generated description that JAWS reads aloud, giving blind users full situational awareness of visual content.

**Screen reader optimized dialogs** — All dialogs use proper focus management, labeled controls, and logical tab order.

---

## Technical Stack

| Component | Technology |
|---|---|
| Language | C# / .NET 8 |
| UI Framework | WPF (Windows Presentation Foundation) |
| Render Engine | FFmpeg with NVENC GPU acceleration |
| Audio | NAudio |
| Video Preview | LibVLC |
| AI Transcription | faster-whisper (large-v3 model) |
| AI Text/Story | Ollama (llama3.2, local inference) |
| Image Generation | Cloudflare Workers AI / Pollinations.ai |
| Stock Media | Pixabay API / Freesound API |
| Screen Reader | JAWS for Windows (primary), NVDA (compatible) |
| Platform | Windows 10/11 (64-bit) |

---

## Core Features

### AIVideoCreator
Generates a complete video from a single audio file (song):
- AI analyzes lyrics and detects mood, theme, and energy level
- Automatically downloads matching stock footage from Pixabay
- Applies mood-based color grading (warm, cool, desaturated, vivid)
- Multi-clip scenes: long scenes use 2–4 different clips for visual variety
- Ken Burns effect with smart crop (subject detection via FFmpeg cropdetect)
- Ambient sound mixing with audio ducking (sidechain compression)
- Karaoke subtitle burn-in with Whisper timestamp sync
- B-roll intelligence for instrumental sections
- Preview list before render — JAWS reads all scenes with timing

### Render Engine
- NVENC GPU-accelerated encoding (RTX/GTX cards)
- Automatic fallback to CPU (libx264) if GPU unavailable
- 4K (3840×2160) output tested on RTX 2060 Max-Q
- Parallel clip processing (up to 4 simultaneous)
- Smart zoompan pipeline — trim before filter prevents 30-minute CPU hangs
- FastRender mode for quick previews

### AI Transcription
- faster-whisper-xxl integration (large-v3 model)
- float16 compute type on CUDA for GPU-accelerated transcription
- SRT subtitle output with timestamp synchronization
- Serbian language support

### Timeline Editor
- Win32 native ListView (JAWS/NVDA compatible out of the box)
- Undo/redo system
- Keyframe animation support
- Audio waveform display
- Export profiles: YouTube 1080p/4K, TikTok 9:16, Instagram 1:1, Compact

---

## Hardware Tested On

- **CPU:** Intel Core i9-10885H
- **GPU:** NVIDIA RTX 2060 Max-Q (NVENC)
- **RAM:** 32GB DDR4 3200MHz
- **Output:** 4K H.264 via NVENC

---

## Installation

### Prerequisites
- Windows 10 or 11 (64-bit)
- [.NET 8.0 Runtime](https://dotnet.microsoft.com/download)
- FFmpeg (place `ffmpeg.exe` in `Ffmpeg\` subfolder)
- VLC media player (for preview)

### Optional (for AI features)
- [Ollama](https://ollama.ai) with `llama3.2` model
- Cloudflare Workers AI API key (for image generation)
- faster-whisper-xxl (for transcription)

---

## Keyboard Shortcuts

| Shortcut | Action |
|---|---|
| Arrow Up/Down | Navigate timeline clips |
| Page Up/Down | Jump 5 clips |
| Ctrl+Space | Play / Pause |
| Ctrl+R / F5 | Render video |
| Ctrl+O | Open media files |
| Ctrl+S | Save project |
| Ctrl+Z / Ctrl+Y | Undo / Redo |
| Ctrl+C / V / X | Copy / Paste / Cut clip |
| Delete | Remove selected clip |
| Ctrl+K | Add keyframe |
| Ctrl+M | Add marker |
| Ctrl+Shift+A | Toggle accessibility mode |
| Menu key / Right-click | Context menu on timeline |

---

## Project Structure

```
UltraVideoEditor/
├── RenderEngine.cs           # FFmpeg pipeline, NVENC, GPU/CPU encoding
├── CinematicProcessor.cs     # Ken Burns, SmartCrop, AudioDucking, VisionAI
├── AIVideoCreator.xaml.cs    # AI video generation engine
├── AITranscription.cs        # faster-whisper integration
├── MainWindow.xaml.cs        # Main UI, Win32 ListView, accessibility
├── Models.cs                 # Data models (TimelineItem, SubtitleItem, etc.)
├── NativeListViewBridge.cs   # Win32 interop for accessible timeline
├── BeatDetection.cs          # Audio beat/rhythm analysis
├── OllamaClient.cs           # Local AI inference (llama3.2)
├── FreesoundClient.cs        # Ambient sound library integration
├── HardwareEncoderDetector.cs # NVENC auto-detection
└── Ffmpeg/
    └── ffmpeg.exe            # (not included, download separately)
```

---

## Why This Matters

There is no professional video editing software that blind users can actually use independently. Adobe Premiere, DaVinci Resolve, Final Cut — none of them work meaningfully with screen readers.

Ultra Creative Suite exists to change that. It is the only editor where a blind person can open the application, import audio, generate a complete video with stock footage, effects, and subtitles, and render to 4K — all without sighted assistance.

This project is being developed as part of an [NLnet Foundation](https://nlnet.nl) grant application under the NGI0 Commons Fund.

---

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines.

## Code of Conduct

See [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md).

## License

GPL-3.0 License — see [LICENSE](LICENSE) file.

---

*Ultra Creative Suite — Because creativity has no boundaries.*
