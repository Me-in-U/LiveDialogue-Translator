using LiveDialogueTranslator.Core.Protocol;

namespace LiveDialogueTranslator.Core.Startup;

public sealed record WorkerStartupState(
    bool PythonAvailable,
    bool LocalWhisperRequested,
    bool FasterWhisperAvailable,
    bool PyannoteAvailable,
    bool DiartAvailable,
    bool TorchAvailable,
    bool SttModelPrepared,
    bool SttModelLoadable,
    bool DiarizationModelPrepared,
    bool DiarizationRequested,
    DiarizationModel DiarizationModel,
    AsrEngine AsrEngine,
    bool QwenAsrAvailable,
    bool WhisperLiveKitAvailable,
    bool WhisperXAvailable,
    bool HasHuggingFaceToken,
    SpeechSeparationModel SpeechSeparationModel = SpeechSeparationModel.None,
    bool SpeechSeparationPackageAvailable = true,
    bool SpeechSeparationModelPrepared = true);

public enum StartupActionKind
{
    InstallPythonPackages,
    PrepareModels
}

public enum StartupCapability
{
    Unavailable,
    NeedsHuggingFaceAccess,
    SttOnly,
    SpeechSeparation,
    FullDiarization
}

public sealed record StartupAction(StartupActionKind Kind, string Title, string Detail);

public sealed record WorkerStartupPlan(
    IReadOnlyList<StartupAction> Actions,
    StartupCapability Capability,
    string? Warning);

public static class WorkerStartupPlanner
{
    public static WorkerStartupPlan CreatePlan(WorkerStartupState state)
    {
        if (!state.PythonAvailable)
        {
            return new WorkerStartupPlan(
                [],
                StartupCapability.Unavailable,
                "Python is required to run local STT and diarization.");
        }

        var actions = new List<StartupAction>();
        var localDiarizationRequested = state.DiarizationRequested;
        var missingPackages =
            (state.LocalWhisperRequested && !state.FasterWhisperAvailable) ||
            MissingDiarizationPackage(state) ||
            MissingAsrEnginePackage(state) ||
            MissingSpeechSeparationPackage(state);

        if (missingPackages)
        {
            actions.Add(new StartupAction(
                StartupActionKind.InstallPythonPackages,
                "Installing local speech packages",
                "Installing faster-whisper, torch, pyannote, and ASR engine speech dependencies."));
        }

        var needsModelPreparation = NeedsModelPreparation(state, localDiarizationRequested);

        // pyannote and diart model files are gated. If they are already cached,
        // the app should start without requiring a saved token; only downloads
        // or validation work must block early for Hugging Face access.
        if (UsesHuggingFaceDiarization(state) && needsModelPreparation && !state.HasHuggingFaceToken)
        {
            return new WorkerStartupPlan(
                [],
                StartupCapability.NeedsHuggingFaceAccess,
                $"Local speaker diarization uses Hugging Face pyannote model files. Accept the required model terms, create an access token, save it, and the app will retry setup.");
        }

        if (needsModelPreparation)
        {
            actions.Add(new StartupAction(
                StartupActionKind.PrepareModels,
                "Preparing speech models",
                "Downloading or validating local Whisper and diarization models."));
        }

        return new WorkerStartupPlan(
            actions,
            state.SpeechSeparationModel != SpeechSeparationModel.None
                ? StartupCapability.SpeechSeparation
                : state.DiarizationRequested
                    ? StartupCapability.FullDiarization
                    : StartupCapability.SttOnly,
            null);
    }

    private static bool MissingAsrEnginePackage(WorkerStartupState state)
    {
        var selectedAsrPackageMissing = state.AsrEngine switch
        {
            AsrEngine.Qwen3Asr => !state.QwenAsrAvailable,
            AsrEngine.WhisperLiveKitSortformer => !state.WhisperLiveKitAvailable,
            AsrEngine.WhisperX => !state.WhisperXAvailable,
            _ => false
        };

        var sortformerPackageMissing = state.DiarizationRequested &&
            state.DiarizationModel == DiarizationModel.Sortformer &&
            !state.WhisperLiveKitAvailable;

        return selectedAsrPackageMissing || sortformerPackageMissing;
    }

    private static bool MissingDiarizationPackage(WorkerStartupState state)
    {
        if (!state.DiarizationRequested)
        {
            return false;
        }

        if (state.DiarizationModel == DiarizationModel.Sortformer)
        {
            return !state.WhisperLiveKitAvailable;
        }

        return !state.PyannoteAvailable ||
            !state.TorchAvailable ||
            (state.DiarizationModel == DiarizationModel.Diart && !state.DiartAvailable);
    }

    private static bool MissingSpeechSeparationPackage(WorkerStartupState state)
    {
        return state.SpeechSeparationModel != SpeechSeparationModel.None &&
            !state.SpeechSeparationPackageAvailable;
    }

    private static bool NeedsModelPreparation(WorkerStartupState state, bool localDiarizationRequested)
    {
        return
            (state.LocalWhisperRequested && (!state.SttModelPrepared || !state.SttModelLoadable)) ||
            (localDiarizationRequested && !state.DiarizationModelPrepared) ||
            (state.SpeechSeparationModel != SpeechSeparationModel.None && !state.SpeechSeparationModelPrepared);
    }

    private static bool UsesHuggingFaceDiarization(WorkerStartupState state)
    {
        return state.DiarizationRequested && state.DiarizationModel is not DiarizationModel.Sortformer;
    }
}
