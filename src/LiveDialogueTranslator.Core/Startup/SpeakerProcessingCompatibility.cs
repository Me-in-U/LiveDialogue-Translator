using LiveDialogueTranslator.Core.Protocol;

namespace LiveDialogueTranslator.Core.Startup;

public enum SpeakerProcessingBlockReason
{
    None,
    WhisperXSortformerRuntime
}

public static class SpeakerProcessingCompatibility
{
    public static SpeakerProcessingBlockReason AssessDiarization(
        AsrEngine asrEngine,
        DiarizationModel diarizationModel)
    {
        return asrEngine == AsrEngine.WhisperX && diarizationModel == DiarizationModel.Sortformer
            ? SpeakerProcessingBlockReason.WhisperXSortformerRuntime
            : SpeakerProcessingBlockReason.None;
    }

    public static bool IsDiarizationSupported(
        AsrEngine asrEngine,
        DiarizationModel diarizationModel)
    {
        return AssessDiarization(asrEngine, diarizationModel) == SpeakerProcessingBlockReason.None;
    }

    public static DiarizationModel ResolveDiarization(
        AsrEngine asrEngine,
        DiarizationModel diarizationModel)
    {
        return IsDiarizationSupported(asrEngine, diarizationModel)
            ? diarizationModel
            : DiarizationModel.PyannoteCommunity;
    }
}
