# Repository Workflow Rules

## Versioning

Use `Major.Minor.Patch`.

- `Major`: platform-wide changes, architecture-wide rewrites, breaking changes, or very large feature sets.
- `Minor`: one feature release. It may contain multiple accepted user-facing features that were integrated through `develop`.
- `Patch`: already-released version hotfix. It may contain one or more tightly related bug, security, packaging, or runtime fixes, but no new user-facing feature.

Version bumps must match the release scope. Do not use a patch release for new functionality. Do not change the final version only because stabilization fixes land in beta before the release is published.

Decide the release version when cutting a beta branch from `develop`:

- If the release contains new user-facing functionality, use the next minor version.
- If the release is only a hotfix for an already published release, use the next patch version.
- If beta scope grows beyond the chosen version meaning, either move the extra work back to `develop` or cut a new correctly named beta branch.

For this app, keep all release version surfaces in sync:

- `Directory.Build.props`
- `installer/LiveDialogueTranslator.iss`
- Git tag and GitHub release name
- Installer artifact name and release notes

## Changelog And Release Notes Rules

Release notes are required for every release candidate and final release.

- Record user-visible features, bug fixes, packaging changes, dependency/runtime changes, and known limitations.
- Keep release notes consistent with the version bump type.
- Do not mix unrelated feature summaries into a patch release note.
- Mention manual validation gaps explicitly.
- If `CHANGELOG.md` or versioned changelog files are introduced, update them in the same commit as the code or release change they describe.

Release notes should include:

- Version number.
- Short summary.
- Added, changed, fixed, and packaging/runtime sections when applicable.
- Validation commands and install checks.
- Release artifact path.

## Branch Model

`main` is the release branch. Use it only for final release integration.

`develop` is the ongoing integration branch for the next release. Feature and normal fix work starts from `develop` and returns to `develop`.

When `develop` is stable enough to prepare a release, create a versioned beta branch from `develop`:

- Format: `v<Major>.<Minor>.<Patch>-beta`
- Example: `v1.2.0-beta`

Feature and normal fix branches must branch from `develop`, not from `main` or beta.

- Feature branch format: `feature/<short-kebab-name>`
- Bug fix branch format: `fix/<short-kebab-name>`
- Release stabilization branch format: `v<Major>.<Minor>.<Patch>-beta`
- Emergency production hotfix format: `hotfix/v<Major>.<Minor>.<Patch>-<short-kebab-name>`

Do not use personal, tool, or automation prefixes in branch names. In particular, do not create branches with prefixes such as `codex/`.

Flow:

1. Keep `main` at the latest stable release.
2. Keep `develop` as the shared integration branch for the next release.
3. Branch feature/fix work from `develop`.
4. Merge completed feature/fix branches back into `develop` after review and verification.
5. When `develop` is stable enough for release preparation, cut `v<Major>.<Minor>.<Patch>-beta` from `develop`.
6. On beta, allow only stabilization work: bug fixes, docs, packaging, dependency lock fixes, release notes, and verification changes.
7. If a beta stabilization fix also applies to future development, merge or cherry-pick it back to `develop`.
8. When beta is final-stable, open the release PR from beta to `main`.
9. After `main` receives the release, tag it as `v<Major>.<Minor>.<Patch>` and publish the release artifact.
10. Sync the released `main` state back into `develop` so future work starts from the released baseline.

Do not add new feature scope directly to beta. New feature work after beta cut goes to `develop` for the next release.

After a branch is merged or no longer needed, delete both local and remote copies.

Branch cleanup rules:

- Never delete `main` or `develop`.
- Delete completed feature/fix branches after they are merged.
- Delete obsolete beta branches after the release is promoted to `main`, unless the user explicitly wants to keep them.
- Delete plan-only branches after the plan has been implemented or superseded.
- Run `git fetch --prune origin` after remote branch deletion.
- Verify remaining local and remote branches with `git branch --all --verbose` and `git ls-remote --heads origin`.

## Main And Release Rules

`main` should stay clean, linear, and release-oriented.

- Do not commit experimental work directly to `main`.
- Do not use `main` as a feature integration branch.
- Do not merge feature/fix branches directly into `main`; merge them through `develop`, then beta, then release to `main`.
- Do not merge planning-only branches into `main`.
- Do not leave merge commits that only expose temporary branch names.
- Prefer rebase, squash, or fast-forward history for develop, beta, and release preparation when it keeps history clearer.
- Only force-push `main` when explicitly requested and after verifying the target commit.

Before cutting beta from `develop`, verify the relevant feature/fix test suites.

Before promoting beta to `main`, verify:

- Python worker tests pass.
- .NET app tests pass.
- The app version is correct.
- The installer builds successfully.
- The installed app smoke check has no import/startup errors.
- Release notes describe the user-visible changes and validation.

## History Rewrite Rules

Treat public history rewrites as release operations.

- Rewrite `main` only when the user explicitly requests it.
- Use `--force-with-lease`, not plain force push.
- Capture the expected remote commit before force pushing.
- Verify local and remote branch pointers after the rewrite.
- Search rewritten history for unwanted branch names, tool prefixes, raw hashes, and obsolete merge commits.
- Do not leave merge commits that expose temporary branches in release history.
- Do not include planning-only commits in release history.

Before and after a history rewrite, check:

```powershell
git status --short --branch
git log --oneline --decorate --max-count=20
git rev-parse main origin/main
git branch --all --verbose
```

## Test Rules

Run the narrowest meaningful test set while developing, then run the full relevant verification before merging, releasing, or claiming completion.

Default test commands for this repository:

```powershell
python worker/test_speaker_worker.py
.\.dotnet-sdk\dotnet.exe run --project tests/LiveDialogueTranslator.Tests/LiveDialogueTranslator.Tests.csproj
```

Packaging and installed-app checks are required when a change affects release output, worker packaging, runtime setup, Python imports, installer metadata, model/runtime paths, or startup behavior:

```powershell
.\scripts\package.ps1
$runtimePython = Join-Path $env:LOCALAPPDATA 'LiveDialogue Translator\runtime\python-3.11.9\python.exe'
& $runtimePython 'C:\Program Files\LiveDialogue Translator\worker\speaker_worker.py' --check --models 'C:\Program Files\LiveDialogue Translator\models'
```

Bug fixes require a regression test whenever practical.

- Reproduce the failing behavior first or identify the smallest existing test gap.
- Add or update a focused test that would fail without the fix.
- Run the focused test first, then the broader relevant suite.
- Do not remove or weaken tests only to make a change pass.

Feature work requires tests for the new behavior, not only compile/build checks.

- Worker behavior belongs in `worker/test_speaker_worker.py`.
- Protocol, settings, transcript, UI contract, startup planning, and packaging behavior belong in `tests/LiveDialogueTranslator.Tests/Program.cs`.
- UI XAML changes should have lightweight structure tests when the behavior is represented by named controls, settings wiring, or visible text.

Release verification must include:

- Python worker test suite.
- .NET test suite.
- Release package build.
- Installed worker smoke check.
- Manual validation for behavior that depends on real audio devices, GPU/runtime packages, external model access, or real speaker audio.

If a test cannot be run, state the reason in the final response, commit body, or PR description. Do not describe skipped tests as passing.

Do not commit, merge, tag, or publish a release with known failing tests unless the user explicitly accepts the risk and the failure is documented.

## Installed App Verification Rules

Do not treat source-tree Python or system Python checks as a substitute for installed-app checks.

Installed-app checks are required when changing:

- `worker/`
- `worker/requirements*.txt`
- `src/LiveDialogueTranslator.App/LiveDialogueTranslator.App.csproj`
- `src/LiveDialogueTranslator.App/Services/PythonRuntimeService.cs`
- `src/LiveDialogueTranslator.App/Services/WorkerEnvironmentService.cs`
- Installer scripts or version metadata.
- Runtime/model path logic.

For installed worker checks, use the app-managed Python runtime:

```powershell
$runtimePython = Join-Path $env:LOCALAPPDATA 'LiveDialogue Translator\runtime\python-3.11.9\python.exe'
& $runtimePython 'C:\Program Files\LiveDialogue Translator\worker\speaker_worker.py' --check --models 'C:\Program Files\LiveDialogue Translator\models'
```

Also verify the installed executable version when packaging or releasing:

```powershell
(Get-Item 'C:\Program Files\LiveDialogue Translator\LiveDialogueTranslator.exe').VersionInfo | Select-Object FileVersion,ProductVersion
```

## Python Worker And Runtime Rules

Worker support files must be packaged with the app.

- When adding a new Python module under `worker/`, update installer/publish include rules in `src/LiveDialogueTranslator.App/LiveDialogueTranslator.App.csproj`.
- Ensure worker sibling imports work under the app-managed Python runtime.
- Keep optional model backends optional in environment checks.
- Do not fail startup only because an unused optional backend is missing.
- Keep worker JSON protocol output stable and line-delimited.
- Do not write non-JSON noise to stdout from worker command paths that the app parses.

When debugging installed worker failures, reproduce with the installed worker path first:

```powershell
& $runtimePython 'C:\Program Files\LiveDialogue Translator\worker\speaker_worker.py' --check --models 'C:\Program Files\LiveDialogue Translator\models'
```

## Dependency Rules

Dependency changes are release-risk changes.

- Keep `worker/requirements*.txt` changes minimal and backend-specific.
- Pin or constrain versions when upstream compatibility is known to be fragile.
- Treat `torch`, CUDA, `pyannote.audio`, `torchcodec`, `diart`, `whisper-livekit`, and `whisperx` changes as high risk.
- Verify setup/install paths after dependency changes, not only unit tests.
- Keep CUDA index behavior explicit when changing torch installation.
- Do not introduce a dependency that requires credentials, system packages, or model downloads without documenting setup behavior.

If dependency installation is intentionally best-effort or optional, document that behavior in tests and release notes.

## Configuration And Protocol Rules

Settings, protocol, and UI must stay aligned.

- When adding an `AppSettings` field, define its default, persistence behavior, and migration behavior.
- When adding a worker configuration field, update protocol serialization tests.
- When changing settings that affect capture startup, verify the setting reaches `WorkerConfiguration`.
- Keep UI control state, persisted settings, and worker payload semantics consistent.
- Preserve compatibility for existing settings files whenever practical.

Protocol changes require tests in `tests/LiveDialogueTranslator.Tests/Program.cs`.

## UI And Localization Rules

UI text changes must be localized.

- Add English and Korean text in `Localizer.cs`.
- Keep visible labels consistent with actual runtime behavior.
- Add structure tests for named controls that are part of settings, protocol wiring, or release-critical UI.
- Do not add explanatory UI text that describes implementation internals.
- Keep settings labels short and behavior-oriented.

When XAML changes affect layout or named controls, verify the relevant tests and inspect the diff for accidental unrelated UI churn.

## Manual QA Rules

Manual QA is required for behavior that cannot be fully proven by unit tests.

Manual QA is especially important for:

- Real microphone/system audio capture.
- Speaker diarization quality.
- GPU/CUDA runtime behavior.
- Hugging Face model access.
- Installed app startup.
- Overlay positioning and click-through behavior.
- End-to-end translation behavior with live captions.

If manual QA is not performed, state that clearly in the final response, commit body, or PR description.

## Commit Rules

Each commit should represent one coherent change.

Commit title:

- Use a concise imperative summary.
- Keep it specific to the behavior or artifact changed.
- Do not include temporary branch names.
- Do not include `codex/` or other tool-specific prefixes.
- Do not include raw commit hashes unless the commit is explicitly reverting or referencing that hash.
- Do not use vague titles such as `update`, `fix`, `wip`, or `changes`.

Commit body:

- Required for non-trivial changes.
- Explain what changed and why.
- Include validation commands or manual checks that were run.
- Mention packaging or install verification when the change affects release behavior.

Recommended body structure:

```text
Summary:
- ...
- ...

Validation:
- ...
```

Keep unrelated work out of the commit. Before staging, inspect `git status --short` and stage only the files that belong to the current change unless the user explicitly asks to commit all current changes.

## Pull Request Rules

Feature and normal fix PRs target `develop`.

Beta stabilization PRs target the current `v<Major>.<Minor>.<Patch>-beta` branch.

Release PRs target `main` and must come from the beta branch.

Emergency hotfix PRs target `main`, then the fix must be merged or cherry-picked back to `develop`.

PR titles should name the version or feature directly and must not include temporary branch names or tool prefixes.

PR descriptions should include:

- Summary of user-visible changes.
- Risk or compatibility notes.
- Test and build evidence.
- Installer or release artifact path when applicable.

Do not open a final release PR until the beta branch has passed the full verification set.

## Release Rules

Release tags use:

- `v<Major>.<Minor>.<Patch>`

Create release tags only on the final `main` release commit.

- Do not tag beta branches with final release tags.
- Do not move a published tag unless the user explicitly requests a tag rewrite.
- If a tag is wrong, verify local and remote tag state before deleting or recreating it.

GitHub release assets should use the installer, not the app executable from the publish folder.

Default release artifact:

- `artifacts/installer/LiveDialogueTranslatorSetup-x64.exe`

Do not attach debug builds, temporary packages, or stale installers.

Before uploading a release asset:

- Rebuild the installer.
- Verify the installer path.
- Verify file version and product version.
- Confirm the artifact corresponds to the tagged commit.

## Artifact And Large File Rules

Generated binaries and large runtime assets do not belong in git.

Do not commit:

- `artifacts/`
- publish output folders.
- installers.
- model files.
- runtime Python archives or extracted runtimes.
- local caches.
- logs.
- temporary screenshots unless they are intentional documentation assets.

Use GitHub release assets for installers and other release binaries.

Documentation screenshots under `docs/assets/` are allowed when they are intentional, current, and reasonably sized.

## Security And Secret Rules

Never commit secrets or local credentials.

Do not commit:

- Hugging Face tokens.
- API keys.
- `.env` files with real credentials or local-only overrides.
- local runtime paths containing user secrets.
- model cache metadata that exposes private access tokens.

The tracked `worker/env/*.env` files are allowed because they are non-secret engine presets, not local credential files.

Logs included in issues, commits, PRs, or release notes must not expose tokens. Redact secrets before sharing command output.

## Plan And Documentation Rules

Implementation plans and analysis documents are not release artifacts by default.

- Keep planning-only work out of `main` unless the user explicitly requests it.
- If a plan is needed for future reference, keep it on a clearly named plan branch or convert it into stable user-facing documentation before release.
- Delete obsolete plan branches after the work is implemented and released.

## Safety Rules

- Never overwrite unrelated user changes.
- Never use destructive git commands such as `reset --hard` unless the user explicitly requests that exact operation.
- Use `--force-with-lease`, not plain force push, when rewriting remote history.
- Verify branch pointers after any rebase, force push, or release promotion.
- Prefer exact file staging over broad staging.
- If a command changes public history, state the target branch and verify the resulting remote pointer.

## Final Response Rules

Final responses must reflect actual work and actual verification.

- Mention files changed when useful.
- Report only tests and checks that were actually run.
- State skipped checks and why they were skipped.
- For packaging/release work, include artifact path and installed version evidence.
- For branch/history work, include final branch pointers and remote state.
- Do not describe a task as complete if required verification is missing.
