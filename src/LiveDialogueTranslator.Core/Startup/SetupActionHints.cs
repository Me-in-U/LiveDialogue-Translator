namespace LiveDialogueTranslator.Core.Startup;

public enum SetupActionKind
{
    None,
    InstallWorker,
    HuggingFaceToken
}

public sealed record SetupActionHint(SetupActionKind Kind, string Label)
{
    public static SetupActionHint None { get; } = new(SetupActionKind.None, "");
}

public static class SetupActionHints
{
    public static SetupActionHint ForModelStatus(string stage, string message)
    {
        if (stage.Equals("mock_mode", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Install worker requirements", StringComparison.OrdinalIgnoreCase))
        {
            return new SetupActionHint(SetupActionKind.InstallWorker, "Install");
        }

        return ForText(message);
    }

    public static SetupActionHint ForWorkerError(string code, string message)
    {
        if (code.Equals("hf_token_missing", StringComparison.OrdinalIgnoreCase) ||
            code.Equals("hf_access_denied", StringComparison.OrdinalIgnoreCase))
        {
            return new SetupActionHint(SetupActionKind.HuggingFaceToken, "Set Access");
        }

        if (code.Equals("stt_unavailable", StringComparison.OrdinalIgnoreCase))
        {
            return new SetupActionHint(SetupActionKind.InstallWorker, "Install");
        }

        return ForText(message);
    }

    public static SetupActionHint ForWarning(string warning)
    {
        return ForText(warning);
    }

    public static SetupActionHint ForSetupFailure(string errorText)
    {
        return ForText(errorText);
    }

    private static SetupActionHint ForText(string text)
    {
        if (text.Contains("hf_token_missing", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Hugging Face token", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Hugging Face model", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("pyannote/speaker-diarization-community-1", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("pyannote/segmentation", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("pyannote/embedding", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("user agreement", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("gated repo", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("authorized list", StringComparison.OrdinalIgnoreCase))
        {
            return new SetupActionHint(SetupActionKind.HuggingFaceToken, "Set Access");
        }

        if (text.Contains("worker requirements", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("faster-whisper", StringComparison.OrdinalIgnoreCase))
        {
            return new SetupActionHint(SetupActionKind.InstallWorker, "Install");
        }

        return SetupActionHint.None;
    }
}
