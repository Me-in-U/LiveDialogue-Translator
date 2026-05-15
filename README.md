# LiveDialogue-Translator

LiveDialogue Translator is a local-first Windows desktop captioning app. It captures
system audio and microphone audio directly, runs local speech-to-text, assigns
speaker labels with local diarization, and can show translated captions in the
main window or a transparent overlay.

The project is inspired by
[LiveCaptions-Translator](https://github.com/SakiRinn/LiveCaptions-Translator),
but it does not depend on Windows Live Captions. Audio is processed through the
app's own capture pipeline and Python worker.

## Screenshots

Screenshot assets are managed under `docs/assets/screenshots`. Additional notes
and the overlay placeholder are kept in [docs/screenshots.md](docs/screenshots.md).

### Caption Workspace

![LiveDialogue Translator caption workspace](docs/assets/screenshots/main-captions.png)

The caption workspace is the default screen. It keeps live speaker captions,
translation output, model status, and capture state visible in a compact window.

### Settings

![LiveDialogue Translator settings](docs/assets/screenshots/settings.png)

The settings screen separates audio input, ASR model, diarization, translation,
overlay, model management, and debug controls into compact groups.

### Info

![LiveDialogue Translator info](docs/assets/screenshots/info.png)

The info screen lists the project links, reference project, supported ASR/STT
and diarization backends, license, runtime path, and local data directory.

### Python Console

![LiveDialogue Translator Python console](docs/assets/screenshots/console.png)

The console screen shows Python worker logs separately from captions, with quick
controls for clearing logs and keeping the view pinned to the latest output.

### Overlay

> Overlay screenshot placeholder. Add the final overlay capture to
> `docs/assets/screenshots/overlay.png` and replace this block when the overlay
> image is ready.

## Current Scope

- Capture system audio, microphone audio, or a mixed device.
- Normalize captured audio to 16 kHz mono PCM before worker processing.
- Run a newline-delimited JSON protocol between the WPF app and Python worker.
- Run local ASR/STT with `faster-whisper`, `Qwen3-ASR`, `WhisperLiveKit`, or
  `WhisperX`. `faster-whisper` is the default engine.
- Install optional ASR engines into isolated package folders so engine-specific
  dependencies do not overwrite the base runtime.
- Run local speaker diarization with `pyannote.audio==4.0.4` and
  `pyannote/speaker-diarization-community-1`, `Diart`, or `Sortformer`.
- Translate captions with the no-key Google provider. Other providers are
  currently shown as placeholders.
- Show captions in a compact WPF shell and in a configurable transparent
  overlay.
- Store app settings and downloaded runtime/model files under LocalAppData.
- Follow the Windows UI language at startup. Korean and English strings are
  included.

## Requirements

- Windows desktop environment.
- .NET 8 SDK for development builds.
- Internet access on first setup so the app can download the managed Python
  runtime, Python packages, and selected model files.
- Hugging Face access is required only when local pyannote diarization is used.
  Accept the selected model terms and provide a fine-grained token with
  `User permissions > Repositories > Read access to contents of all public gated
  repos you can access`.
- NVIDIA CUDA is optional. The app can install CUDA-enabled PyTorch when an
  NVIDIA GPU is detected.

End users do not need to install Python manually. On first Start or Prepare, the
app downloads the official Python 3.11.9 x64 embeddable runtime from python.org,
extracts it under `%LOCALAPPDATA%\LiveDialogue Translator\runtime\python-3.11.9`,
bootstraps pip, and installs the worker dependencies there.

## Quick Start

1. Build or package the app with the commands below.
2. Launch `LiveDialogueTranslator.exe`.
3. Open Model Manager if the app asks for Hugging Face access.
4. Choose the ASR model, diarization mode, input source, and translation target.
5. Press Start capture.

The first run can take several minutes because the app prepares Python packages
and model files. Later runs reuse the LocalAppData runtime and cache.

## Build and Package

This repository can use a repo-local SDK at `.dotnet-sdk\dotnet.exe` or a system
`dotnet` SDK.

```powershell
.\.dotnet-sdk\dotnet.exe run --project tests\LiveDialogueTranslator.Tests\LiveDialogueTranslator.Tests.csproj
.\.dotnet-sdk\dotnet.exe build src\LiveDialogueTranslator.App\LiveDialogueTranslator.App.csproj -c Release
.\scripts\package.ps1
```

`scripts\package.ps1` publishes to `publish\win-x64`. If Inno Setup is
installed, it also creates `artifacts\installer\LiveDialogueTranslatorSetup-x64.exe`.

## Manual Worker Testing

For developer-only worker testing, use the app-managed runtime after it has been
prepared:

```powershell
%LOCALAPPDATA%\LiveDialogue Translator\runtime\python-3.11.9\python.exe -m pip install --no-warn-script-location -r worker\requirements.txt
%LOCALAPPDATA%\LiveDialogue Translator\runtime\python-3.11.9\python.exe worker\speaker_worker.py
```

The worker reads JSON commands from stdin and writes JSON events to stdout. Use
the WPF app for normal operation because it owns audio capture, model setup,
translation, and worker lifecycle management.

## Repository Layout

- `src\LiveDialogueTranslator.App` - WPF desktop app, overlay window, settings, and
  worker orchestration.
- `src\LiveDialogueTranslator.Core` - shared protocol, startup planning, transcript,
  speaker, runtime, and history logic.
- `worker` - Python speech worker, package requirements, and engine environment
  presets.
- `tests\LiveDialogueTranslator.Tests` - lightweight executable tests.
- `scripts` - packaging helpers.
- `installer` - Inno Setup installer definition.

## Privacy

Audio is processed locally by the Python worker. Hugging Face is contacted only
to download gated model files after you provide a token. Translation requests are
sent to the selected translation provider.

## Credits

- [LiveCaptions-Translator](https://github.com/SakiRinn/LiveCaptions-Translator)
  for the original real-time caption translation reference.
- [faster-whisper](https://github.com/SYSTRAN/faster-whisper) for local Whisper
  inference.
- [Qwen3-ASR](https://huggingface.co/Qwen/Qwen3-ASR-1.7B) for Qwen ASR model
  support.
- [WhisperLiveKit](https://github.com/QuentinFuxa/WhisperLiveKit) for
  streaming Whisper + Sortformer support.
- [WhisperX](https://github.com/m-bain/whisperX) for WhisperX ASR support.
- [pyannote.audio](https://github.com/pyannote/pyannote-audio) for Community-1
  speaker diarization.
- [Diart](https://github.com/juanmc2005/diart) for realtime diarization.
- [Sortformer](https://huggingface.co/nvidia/diar_streaming_sortformer_4spk-v2)
  for streaming diarization support.

## License

LiveDialogue Translator is licensed under the Apache License 2.0. See
[LICENSE](LICENSE).
