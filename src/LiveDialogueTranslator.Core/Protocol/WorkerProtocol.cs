using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiveDialogueTranslator.Core.Protocol;

public enum InputMode
{
    SystemAndMic,
    SystemAudioOnly,
    MixedDevice
}

public enum ComputeMode
{
    Auto,
    Cpu,
    Cuda
}

public enum DiarizationModel
{
    PyannoteCommunity,
    Diart,
    Sortformer
}

public enum SpeakerCountMode
{
    ActiveMax,
    Exact,
    SessionMax
}

public enum AsrEngine
{
    None,
    Qwen3Asr,
    WhisperLiveKitSortformer,
    WhisperX
}

public sealed record WorkerConfiguration(
    InputMode InputMode,
    string SttModel,
    IReadOnlyList<string> SttLanguages,
    int SttQualityPreset,
    ComputeMode ComputeMode,
    bool DiarizationEnabled,
    DiarizationModel DiarizationModel,
    int MaxSpeakers,
    int? ExactSpeakers,
    bool ShowLatency,
    IReadOnlyDictionary<string, string> SpeakerNames,
    bool DiartManualSettings = false,
    double? DiartDurationSeconds = null,
    double? DiartStepSeconds = null,
    double? DiartLatencySeconds = null,
    double? DiartTauActive = null,
    double? DiartRhoUpdate = null,
    double? DiartDeltaNew = null,
    int DiarizationQualityPreset = 100,
    AsrEngine AsrEngine = AsrEngine.None,
    SpeakerCountMode SpeakerCountMode = SpeakerCountMode.ActiveMax);

public sealed record WorkerCommand(string Type, IReadOnlyDictionary<string, object?> Payload);

public interface IWorkerEvent
{
    string Type { get; }
}

public sealed record PartialCaptionEvent(
    string SpeakerId,
    string Text,
    long StartMs,
    long EndMs,
    int? LatencyMs) : IWorkerEvent
{
    public string Type => "partial_caption";
}

public sealed record FinalCaptionEvent(
    string SpeakerId,
    string Text,
    long StartMs,
    long EndMs,
    int? LatencyMs) : IWorkerEvent
{
    public string Type => "final_caption";
}

public sealed record SpeakerSegmentEvent(
    string SpeakerId,
    long StartMs,
    long EndMs,
    double Confidence) : IWorkerEvent
{
    public string Type => "speaker_segment";
}

public sealed record ModelStatusEvent(
    string Stage,
    string Message,
    double? Progress) : IWorkerEvent
{
    public string Type => "model_status";
}

public sealed record LatencyEvent(
    string Stage,
    int LatencyMs) : IWorkerEvent
{
    public string Type => "latency";
}

public sealed record WorkerErrorEvent(
    string Code,
    string Message,
    bool Recoverable) : IWorkerEvent
{
    public string Type => "error";
}

public static class WorkerProtocol
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static WorkerCommand Configure(WorkerConfiguration configuration)
    {
        var payload = new Dictionary<string, object?>
        {
            ["inputMode"] = FormatInputMode(configuration.InputMode),
            ["sttModel"] = configuration.SttModel,
            ["sttLanguages"] = configuration.SttLanguages,
            ["sttQualityPreset"] = configuration.SttQualityPreset,
            ["diarizationQualityPreset"] = configuration.DiarizationQualityPreset,
            ["computeMode"] = FormatComputeMode(configuration.ComputeMode),
            ["diarizationEnabled"] = configuration.DiarizationEnabled,
            ["diarizationModel"] = FormatDiarizationModel(configuration.DiarizationModel),
            ["maxSpeakers"] = configuration.MaxSpeakers,
            ["speakerCountMode"] = FormatSpeakerCountMode(configuration.SpeakerCountMode),
            ["showLatency"] = configuration.ShowLatency,
            ["speakerNames"] = configuration.SpeakerNames,
            ["asrEngine"] = FormatAsrEngine(configuration.AsrEngine),
            ["diartManualSettings"] = configuration.DiartManualSettings
        };

        if (configuration.ExactSpeakers.HasValue)
        {
            payload["exactSpeakers"] = configuration.ExactSpeakers.Value;
        }

        if (configuration.DiartManualSettings)
        {
            payload["diartDurationSeconds"] = configuration.DiartDurationSeconds;
            payload["diartStepSeconds"] = configuration.DiartStepSeconds;
            payload["diartLatencySeconds"] = configuration.DiartLatencySeconds;
            payload["diartTauActive"] = configuration.DiartTauActive;
            payload["diartRhoUpdate"] = configuration.DiartRhoUpdate;
            payload["diartDeltaNew"] = configuration.DiartDeltaNew;
        }

        return new WorkerCommand("configure", payload);
    }

    public static WorkerCommand Start() => new("start", new Dictionary<string, object?>());

    public static WorkerCommand Stop() => new("stop", new Dictionary<string, object?>());

    public static WorkerCommand AudioChunk(string source, long timestampMs, ReadOnlyMemory<byte> pcm16Mono16Khz)
    {
        return new WorkerCommand("audio_chunk", new Dictionary<string, object?>
        {
            ["source"] = source,
            ["timestampMs"] = timestampMs,
            ["format"] = "pcm_s16le_16khz_mono",
            ["data"] = Convert.ToBase64String(pcm16Mono16Khz.Span)
        });
    }

    public static string Serialize(WorkerCommand command)
    {
        var envelope = new Dictionary<string, object?>
        {
            ["type"] = command.Type
        };

        foreach (var item in command.Payload)
        {
            envelope[item.Key] = item.Value;
        }

        return JsonSerializer.Serialize(envelope, JsonOptions) + "\n";
    }

    public static IWorkerEvent ParseEvent(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var type = root.GetProperty("type").GetString();

        return type switch
        {
            "partial_caption" => new PartialCaptionEvent(
                RequiredString(root, "speakerId"),
                RequiredString(root, "text"),
                RequiredInt64(root, "startMs"),
                RequiredInt64(root, "endMs"),
                OptionalInt32(root, "latencyMs")),
            "final_caption" => new FinalCaptionEvent(
                RequiredString(root, "speakerId"),
                RequiredString(root, "text"),
                RequiredInt64(root, "startMs"),
                RequiredInt64(root, "endMs"),
                OptionalInt32(root, "latencyMs")),
            "speaker_segment" => new SpeakerSegmentEvent(
                RequiredString(root, "speakerId"),
                RequiredInt64(root, "startMs"),
                RequiredInt64(root, "endMs"),
                OptionalDouble(root, "confidence") ?? 0),
            "model_status" => new ModelStatusEvent(
                RequiredString(root, "stage"),
                RequiredString(root, "message"),
                OptionalDouble(root, "progress")),
            "latency" => new LatencyEvent(
                RequiredString(root, "stage"),
                RequiredInt32(root, "latencyMs")),
            "error" => new WorkerErrorEvent(
                RequiredString(root, "code"),
                RequiredString(root, "message"),
                root.TryGetProperty("recoverable", out var recoverable) && recoverable.GetBoolean()),
            _ => throw new InvalidOperationException($"Unsupported worker event type '{type}'.")
        };
    }

    public static string FormatInputMode(InputMode mode)
    {
        return mode switch
        {
            InputMode.SystemAndMic => "system_and_mic",
            InputMode.SystemAudioOnly => "system_audio_only",
            InputMode.MixedDevice => "mixed_device",
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
        };
    }

    public static string FormatComputeMode(ComputeMode mode)
    {
        return mode switch
        {
            ComputeMode.Auto => "auto",
            ComputeMode.Cpu => "cpu",
            ComputeMode.Cuda => "cuda",
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
        };
    }

    public static string FormatDiarizationModel(DiarizationModel model)
    {
        return model switch
        {
            DiarizationModel.PyannoteCommunity => "pyannote_community",
            DiarizationModel.Diart => "diart",
            DiarizationModel.Sortformer => "sortformer",
            _ => throw new ArgumentOutOfRangeException(nameof(model), model, null)
        };
    }

    public static string FormatAsrEngine(AsrEngine engine)
    {
        return engine switch
        {
            AsrEngine.None => "faster_whisper",
            AsrEngine.Qwen3Asr => "qwen3_asr_diarization",
            AsrEngine.WhisperLiveKitSortformer => "whisperlivekit_sortformer",
            AsrEngine.WhisperX => "whisperx",
            _ => throw new ArgumentOutOfRangeException(nameof(engine), engine, null)
        };
    }

    public static string FormatSpeakerCountMode(SpeakerCountMode mode)
    {
        return mode switch
        {
            SpeakerCountMode.Exact => "exact",
            SpeakerCountMode.SessionMax => "session_max",
            SpeakerCountMode.ActiveMax => "active_max",
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
        };
    }

    private static string RequiredString(JsonElement root, string name)
    {
        return root.GetProperty(name).GetString()
            ?? throw new InvalidOperationException($"Missing '{name}'.");
    }

    private static int RequiredInt32(JsonElement root, string name)
    {
        return root.GetProperty(name).GetInt32();
    }

    private static long RequiredInt64(JsonElement root, string name)
    {
        return root.GetProperty(name).GetInt64();
    }

    private static int? OptionalInt32(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetInt32()
            : null;
    }

    private static double? OptionalDouble(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetDouble()
            : null;
    }
}
