# Overlapping Speech Separation Design

## Goal

Separate two simultaneous speakers from captured system audio, transcribe each
speaker with the selected multilingual ASR engine, and keep the existing
translation flow within a five-second end-to-end target on supported hardware.

The target is a latency budget, not a universal guarantee. Model size, GPU
contention, translation provider latency, and network conditions remain outside
the separator's control.

## Supported models

Only models with a public 16 kHz two-speaker checkpoint and a reproducible
Python 3.11 inference package are selectable.

| Model | Runtime package | Checkpoint | App minimum | Recommendation |
| --- | --- | --- | --- | --- |
| MossFormer2_SS_16K | ClearVoice 0.1.2 | [alibabasglab/MossFormer2_SS_16K](https://huggingface.co/alibabasglab/MossFormer2_SS_16K) | NVIDIA CUDA, 10 GB VRAM, 16 GB RAM | First choice when memory permits |
| SepFormer WHAMR16k | SpeechBrain 1.0.3 | [speechbrain/sepformer-whamr16k](https://huggingface.co/speechbrain/sepformer-whamr16k) | NVIDIA CUDA, 6 GB VRAM, 16 GB RAM | Lower-memory fallback |

The following candidates are not exposed: RE-SepFormer, SkiM, SepReformer,
SR-CorrNet, and TF-GridNet. They do not currently provide the complete
combination required by this app: a maintained installable inference path,
public 16 kHz checkpoint, Windows Python 3.11 compatibility, and integration
that can be prepared and checked automatically.

## Hardware recommendation policy

At startup and when the user selects Detect again, the app reads:

- CPU model and logical processor count
- physical system memory
- NVIDIA GPU name and total VRAM through `nvidia-smi`
- selected Compute mode, ASR engine, and ASR model

Auto reserves memory for the selected ASR model, then chooses MossFormer2 when
its combined threshold is met, otherwise SepFormer when its lower threshold is
met. CPU mode, missing NVIDIA CUDA, insufficient memory, and WhisperLiveKit
result in Off. A manually selected model that is no longer supported by the
current hardware is reset to Auto.

The selector shows the effective automatic choice in its closed state, such as
`Auto: MossFormer2`. Both integrated models remain visible in the drop-down.
A model that cannot run with the current ASR and hardware combination is
disabled instead of disappearing, and its tooltip shows the required VRAM or
RAM. This separates product support from compatibility with the current
configuration.

WhisperLiveKit is excluded because it owns one stateful streaming ASR session.
Feeding two independently separated streams into that one session would mix
state and timestamps.

## Runtime data flow

```text
Windows system audio, 16 kHz mono PCM
  -> 1.75 second capture buffer
  -> prepend previous 250 ms input tail
  -> selected two-speaker separator
  -> correlate the repeated prefix with previous output tails
  -> keep or swap stems to stabilize channel order
  -> remove the repeated 250 ms prefix
  -> reject weak, highly correlated, or duplicate stems
  -> skip Qwen ForcedAligner because separated channels already define speakers
  -> batch both retained stems into one Qwen inference when Qwen is selected
  -> otherwise transcribe each retained stem with the selected multilingual ASR
  -> emit stable speaker caption events
  -> existing target-language translation provider
  -> caption page and overlay
```

The first window contains 1.75 seconds. Later separator inputs contain the new
1.75 seconds plus the previous 250 ms, producing a two-second model window
without losing or repeating caption audio.

The worker applies the selected CUDA device to the ClearVoice network instead
of relying on the package's automatic device choice. If processing falls behind
capture, the queue keeps only the newest pending window for each audio source.
This bounds live-caption delay instead of replaying an ever-growing stale queue.

## Speaker identity and diarization interaction

The separation models produce exactly two streams. The worker assigns stable
`speaker_1` and `speaker_2` identities after channel-order correction. The
microphone remains `mic`.

When overlap separation is active, acoustic diarization is not loaded for the
same capture. This avoids duplicate speaker decisions and prevents optional
runtime dependency conflicts. When separation is Off, the existing Community-1,
Diart, or Sortformer diarization path is unchanged.

The Settings page groups both choices under Speaker Processing. It calls
MossFormer2 and SepFormer overlapping voice separation, and calls Community-1,
Diart, and Sortformer general speaker identification. Enabling an overlapping
voice separator pauses and disables the general identification controls while
preserving their saved values for later use. A live summary states which path
will actually run before capture starts.

## Multilingual behavior

The separator operates on waveforms and has no language selection. Each stem
uses the app's existing ASR language configuration. The translated result uses
the same selected target language and provider as a normal caption. Korean,
English, and mixed multilingual sessions therefore follow the same ASR and
translation rules as non-overlapping audio.

## Five-second target budget

This is the intended engineering budget for the recommended CUDA path:

| Stage | Target budget |
| --- | ---: |
| Audio accumulation | 1.75 s |
| Separation and channel stabilization | 0.75 s |
| Two-stem ASR inference | 1.50 s |
| Translation | 0.75 s |
| Event and UI rendering | 0.25 s |
| Total | 5.00 s |

The worker reports ASR queue, start, finish, and caption latency events. A real
audio corpus and installed CUDA runtime are still required before claiming the
budget is achieved on a specific machine.

## Installation and failure handling

Each separator has an isolated package directory under LocalAppData. Startup
checks package availability and model preparation separately. Missing packages
are installed before checkpoint preparation. Failed model loading prevents the
worker from entering Listening and reports a recoverable setup error.

If a separator call fails during capture, the worker reports the error and
falls back to the original mixed-audio ASR path for that window. If the stems do
not contain credible simultaneous voices, the normal mixed-audio path is used
to avoid duplicated or degraded captions.

## Validation boundary

Automated coverage verifies recommendation thresholds, protocol persistence,
startup planning, runtime package selection, two-second window construction,
channel permutation correction, independent two-stem captions, checkpoint
state checks, and exclusion of unsupported model names. Package resolver checks
also confirm that both pinned integrations resolve for Python 3.11 on Windows.

End-to-end quality and latency must additionally be evaluated with real
multilingual overlap recordings after the selected checkpoints are installed.
