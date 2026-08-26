using LiveDialogueTranslator.Core.Protocol;

namespace LiveDialogueTranslator.Core.Runtime;

public sealed record HardwareProfile(
    string CpuName,
    int LogicalProcessorCount,
    long MemoryBytes,
    string? GpuName,
    long GpuMemoryBytes,
    bool NvidiaDriverAvailable)
{
    public static HardwareProfile Unknown { get; } = new(
        "Unknown CPU",
        Math.Max(1, Environment.ProcessorCount),
        0,
        null,
        0,
        false);

    public bool HasNvidiaGpu => NvidiaDriverAvailable && !string.IsNullOrWhiteSpace(GpuName);
    public double MemoryGiB => MemoryBytes / 1024d / 1024d / 1024d;
    public double GpuMemoryGiB => GpuMemoryBytes / 1024d / 1024d / 1024d;
}

public sealed record SpeechSeparationOption(
    SpeechSeparationModel Model,
    string DisplayName,
    long MinimumGpuMemoryBytes,
    long MinimumSystemMemoryBytes);

public sealed record SpeechSeparationRecommendation(
    SpeechSeparationModel Model,
    IReadOnlyList<SpeechSeparationModel> SupportedModels,
    string Reason)
{
    public bool IsAvailable => Model != SpeechSeparationModel.None;
}

public static class SpeechSeparationAdvisor
{
    public const long GiB = 1024L * 1024L * 1024L;

    public static IReadOnlyList<SpeechSeparationOption> Catalog { get; } =
    [
        new(
            SpeechSeparationModel.MossFormer2,
            "MossFormer2_SS_16K",
            10 * GiB,
            16 * GiB),
        new(
            SpeechSeparationModel.SepFormerWhamr16k,
            "SepFormer WHAMR16k",
            6 * GiB,
            16 * GiB)
    ];

    public static SpeechSeparationRecommendation Recommend(
        HardwareProfile profile,
        ComputeMode computeMode,
        AsrEngine asrEngine,
        string? sttModel = null)
    {
        if (computeMode == ComputeMode.Cpu)
        {
            return Disabled("CPU mode cannot meet the five-second separation and translation target.");
        }

        if (asrEngine == AsrEngine.WhisperLiveKitSortformer)
        {
            return Disabled("WhisperLiveKit uses one stateful streaming session and cannot safely consume two separated channels.");
        }

        if (!profile.HasNvidiaGpu)
        {
            return Disabled("A CUDA-capable NVIDIA GPU is required for the five-second target.");
        }

        var asrGpuMemory = RequiredAsrGpuMemory(asrEngine, sttModel);
        var asrSystemMemory = RequiredAsrSystemMemory(asrEngine, sttModel);
        var supported = Catalog
            .Where(option =>
                profile.GpuMemoryBytes >= Math.Max(
                    option.MinimumGpuMemoryBytes,
                    asrGpuMemory + SeparationWorkingMemory(option.Model)) &&
                profile.MemoryBytes >= Math.Max(option.MinimumSystemMemoryBytes, asrSystemMemory))
            .Select(option => option.Model)
            .ToArray();

        if (supported.Contains(SpeechSeparationModel.MossFormer2))
        {
            return new SpeechSeparationRecommendation(
                SpeechSeparationModel.MossFormer2,
                supported,
                "MossFormer2_SS_16K is recommended for the available CUDA GPU and memory.");
        }

        if (supported.Contains(SpeechSeparationModel.SepFormerWhamr16k))
        {
            return new SpeechSeparationRecommendation(
                SpeechSeparationModel.SepFormerWhamr16k,
                supported,
                "SepFormer WHAMR16k is recommended because it has the lower GPU memory requirement.");
        }

        return Disabled("No supported separation model has enough GPU and system memory for the five-second target.");
    }

    public static SpeechSeparationModel Resolve(
        SpeechSeparationModel requested,
        SpeechSeparationRecommendation recommendation)
    {
        if (requested == SpeechSeparationModel.Auto)
        {
            return recommendation.Model;
        }

        return recommendation.SupportedModels.Contains(requested)
            ? requested
            : SpeechSeparationModel.None;
    }

    public static string DisplayName(SpeechSeparationModel model)
    {
        return model switch
        {
            SpeechSeparationModel.Auto => "Auto",
            SpeechSeparationModel.None => "Off",
            SpeechSeparationModel.MossFormer2 => "MossFormer2_SS_16K",
            SpeechSeparationModel.SepFormerWhamr16k => "SepFormer WHAMR16k",
            _ => model.ToString()
        };
    }

    private static SpeechSeparationRecommendation Disabled(string reason)
    {
        return new SpeechSeparationRecommendation(
            SpeechSeparationModel.None,
            [],
            reason);
    }

    private static long RequiredAsrGpuMemory(AsrEngine engine, string? sttModel)
    {
        var model = (sttModel ?? string.Empty).Trim().ToLowerInvariant();
        return engine switch
        {
            AsrEngine.Qwen3Asr when model.Contains("1.7b", StringComparison.Ordinal) => 10 * GiB,
            AsrEngine.Qwen3Asr => 6 * GiB,
            AsrEngine.WhisperX when IsLargeWhisperModel(model) => 8 * GiB,
            AsrEngine.WhisperX => 5 * GiB,
            _ when IsLargeWhisperModel(model) => 6 * GiB,
            _ => 3 * GiB
        };
    }

    private static long RequiredAsrSystemMemory(AsrEngine engine, string? sttModel)
    {
        var model = (sttModel ?? string.Empty).Trim().ToLowerInvariant();
        return engine switch
        {
            AsrEngine.Qwen3Asr when model.Contains("1.7b", StringComparison.Ordinal) => 32 * GiB,
            AsrEngine.Qwen3Asr => 24 * GiB,
            AsrEngine.WhisperX when IsLargeWhisperModel(model) => 24 * GiB,
            _ => 16 * GiB
        };
    }

    private static long SeparationWorkingMemory(SpeechSeparationModel model) => model switch
    {
        SpeechSeparationModel.MossFormer2 => 3 * GiB,
        SpeechSeparationModel.SepFormerWhamr16k => 3 * GiB,
        _ => 0
    };

    private static bool IsLargeWhisperModel(string model)
    {
        return model.Contains("medium", StringComparison.Ordinal) ||
            model.Contains("large", StringComparison.Ordinal);
    }
}
