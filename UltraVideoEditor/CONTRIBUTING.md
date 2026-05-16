# Contributing to Ultra Creative Suite

Thank you for your interest in contributing to Ultra Creative Suite.

This project exists to make video editing accessible to blind and visually impaired users. Every contribution — code, documentation, testing, or feedback — directly helps people who currently have no professional video editing tools available to them.

---

## Before You Start

Please read the [Code of Conduct](CODE_OF_CONDUCT.md). All contributors are expected to follow it.

---

## How to Contribute

### Reporting Bugs

Open an issue with:
- A clear description of the problem
- Steps to reproduce
- Expected vs actual behavior
- Log output (copy from the log window in the app)
- Your system: Windows version, .NET version, GPU model
- Screen reader being used (JAWS version, NVDA version, or N/A)

### Accessibility Issues

Accessibility bugs are **highest priority**. If something does not work correctly with JAWS or NVDA, please open an issue immediately with the label `accessibility`.

Include:
- Screen reader name and version
- What JAWS/NVDA announced
- What it should have announced
- Which control or dialog is affected

### Suggesting Features

Open an issue with the label `enhancement`. Describe:
- What the feature does
- Why it matters for accessibility
- How a blind user would interact with it

### Submitting Code

1. Fork the repository
2. Create a branch: `git checkout -b fix/your-description`
3. Make your changes
4. Test with a screen reader if possible
5. Submit a pull request with a clear description

---

## Code Guidelines

**Language:** C# / .NET 8 / WPF

**Comments:** Write comments in Serbian (ekavica dialect) — this is the project convention.

**Accessibility first:** Every UI change must be tested for screen reader compatibility. If you add a new control, it must have:
- A proper `AutomationProperties.Name`
- Correct tab order
- Keyboard accessibility (no mouse-only interactions)

**No breaking changes to the Win32 ListView bridge** — this is the core accessibility component. Changes here require extra review.

**FFmpeg commands:** Document any new FFmpeg filter chains with comments explaining what each parameter does and why.

**Logging:** Use `LogToMainWindow()` for all significant operations. Blind users rely on the log to understand what the application is doing.

---

## Development Setup

1. Install Visual Studio 2022 or later
2. Install .NET 8 SDK
3. Clone the repository
4. Download FFmpeg and place `ffmpeg.exe` in `bin\Debug\net8.0-windows\Ffmpeg\`
5. Install VLC media player (for LibVLC preview)
6. Build and run

Optional for AI features:
- Install [Ollama](https://ollama.ai) and pull `llama3.2`
- Get a Cloudflare Workers AI API key
- Install faster-whisper-xxl

---

## Priority Areas

These areas need the most help:

- **Accessibility testing** with different screen readers and Windows versions
- **Documentation** in English for international contributors
- **Linux/macOS port** investigation (currently Windows-only)
- **Performance** — render pipeline optimization
- **Localization** — the UI is currently in Serbian/Bosnian, English support needed

---

## Questions

Open an issue with the label `question`. We read everything.
