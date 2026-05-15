namespace LiveDialogueTranslator.Core.Startup;

public static class HuggingFaceLinks
{
    public const string AccessTokensUrl = "https://huggingface.co/settings/tokens";
    public const string CommunityModelId = "pyannote/speaker-diarization-community-1";
    public const string DiartSegmentationModelId = "pyannote/segmentation";
    public const string DiartEmbeddingModelId = "pyannote/embedding";
    public const string CommunityModelUrl = "https://huggingface.co/pyannote/speaker-diarization-community-1";
    public const string DiartSegmentationModelUrl = "https://huggingface.co/pyannote/segmentation";
    public const string DiartEmbeddingModelUrl = "https://huggingface.co/pyannote/embedding";
    public const string RequiredFineGrainedPermission = "Read access to contents of all public gated repos you can access";
    public const string TokenPermissionSummary = "Fine-grained token: User permissions > Repositories > Read access to contents of all public gated repos you can access. No Write, Inference, Billing, Jobs, or Org permissions are needed.";
}
