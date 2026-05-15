# Contributing

Thanks for taking time to improve LiveDialogue Translator.

## Branch Workflow

Use `develop` as the target branch for normal contributions.

- Feature work targets `develop`.
- Normal bug fixes target `develop`.
- Release stabilization fixes target the active `v<Major>.<Minor>.<Patch>-beta` branch.
- Emergency hotfixes may target `main`, but must be merged or cherry-picked back to `develop`.

Do not open feature or normal fix pull requests directly against `main`. The `main` branch is reserved for final release integration.

## Before Opening A Pull Request

Keep each pull request focused on one coherent change. Avoid mixing unrelated refactors, formatting churn, and feature work.

For code changes, run the relevant tests before submitting:

```powershell
python worker/test_speaker_worker.py
.\.dotnet-sdk\dotnet.exe run --project tests/LiveDialogueTranslator.Tests\LiveDialogueTranslator.Tests.csproj
```

For packaging, runtime, installer, worker import, or startup changes, also run the package and installed-app checks described in `AGENT.md`.

## Development Notes

- Local ASR and diarization changes often depend on optional model packages. Keep optional backends optional.
- Worker stdout is parsed as newline-delimited JSON by the WPF app. Do not write non-JSON diagnostic output to stdout from worker command paths.
- UI text changes must update both English and Korean localization entries.
- Do not commit generated installers, publish output, model files, runtime archives, local caches, or logs.
- Do not commit Hugging Face tokens, API keys, or local credential files.

## Pull Request Requirements

Pull requests should include:

- A short summary of the user-visible change.
- Risk or compatibility notes.
- Test and build evidence.
- Installer or release artifact path when applicable.
- A note for any relevant test that could not be run.
