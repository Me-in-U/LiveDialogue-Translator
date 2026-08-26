using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using LiveDialogueTranslator.App.Models;
using LiveDialogueTranslator.Core.Protocol;

namespace LiveDialogueTranslator.App.Services;

public sealed class SettingsStore
{
    private readonly string path;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public SettingsStore(string path)
    {
        this.path = path;
    }

    public AppSettings Load()
    {
        if (!File.Exists(path))
        {
            var settings = new AppSettings();
            NormalizeSettings(settings);
            return settings;
        }

        try
        {
            var json = NormalizeLegacyRemovedSpeechModels(File.ReadAllText(path));
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            settings.Overlay ??= OverlayWindowSettings.Default();
            NormalizeSettings(settings);
            return settings;
        }
        catch
        {
            var backup = path + ".bak";
            File.Copy(path, backup, overwrite: true);
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        NormalizeSettings(settings);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(settings, JsonOptions));
    }

    public WorkerConfiguration ToWorkerConfiguration(
        AppSettings settings,
        SpeechSeparationModel? effectiveSpeechSeparationModel = null)
    {
        var exactSpeakers = settings.SpeakerCountMode == SpeakerCountMode.Exact
            ? Math.Max(1, settings.ExactSpeakers ?? settings.MaxSpeakers)
            : (int?)null;
        var maxSpeakers = exactSpeakers ?? settings.MaxSpeakers;
        return new WorkerConfiguration(
            settings.InputMode,
            settings.SttModel,
            settings.SttLanguages,
            settings.SttQualityPreset,
            settings.ComputeMode,
            settings.DiarizationEnabled &&
                (effectiveSpeechSeparationModel ?? settings.SpeechSeparationModel) == SpeechSeparationModel.None,
            settings.DiarizationModel,
            maxSpeakers,
            exactSpeakers,
            settings.ShowLatency,
            new Dictionary<string, string>(),
            settings.DiartManualSettings,
            settings.DiartDurationSeconds,
            settings.DiartStepSeconds,
            settings.DiartLatencySeconds,
            settings.DiartTauActive,
            settings.DiartRhoUpdate,
            settings.DiartDeltaNew,
            DiarizationQualityPreset: settings.DiarizationQualityPreset,
            AsrEngine: settings.AsrEngine,
            SpeakerCountMode: settings.SpeakerCountMode,
            SpeechSeparationModel: effectiveSpeechSeparationModel ?? settings.SpeechSeparationModel);
    }

    private static void NormalizeSettings(AppSettings settings)
    {
        settings.SttQualityPreset = settings.SttQualityPreset >= 75 ? 100 : settings.SttQualityPreset >= 35 ? 50 : 0;
        settings.DiarizationQualityPreset = settings.DiarizationQualityPreset >= 75 ? 100 : settings.DiarizationQualityPreset >= 35 ? 50 : 0;
        if (!Enum.IsDefined(settings.SpeakerCountMode))
        {
            settings.SpeakerCountMode = SpeakerCountMode.ActiveMax;
        }
        if (!Enum.IsDefined(settings.SpeechSeparationModel))
        {
            settings.SpeechSeparationModel = SpeechSeparationModel.Auto;
        }
        if (settings.ExactSpeakers is > 0)
        {
            settings.MaxSpeakers = settings.ExactSpeakers.Value;
            settings.SpeakerCountMode = SpeakerCountMode.Exact;
        }
        if (settings.SpeakerCountMode == SpeakerCountMode.Exact)
        {
            settings.ExactSpeakers = settings.ExactSpeakers is > 0 ? settings.ExactSpeakers.Value : settings.MaxSpeakers;
        }
        else
        {
            settings.ExactSpeakers = null;
        }
        settings.DisplayLines = NormalizeLineCount(settings.DisplayLines);
        settings.CaptionDisplayLines = settings.CaptionDisplayLines > 0
            ? NormalizeLineCount(settings.CaptionDisplayLines)
            : settings.DisplayLines;
        settings.OverlayDisplayLines = settings.OverlayDisplayLines > 0
            ? NormalizeLineCount(settings.OverlayDisplayLines)
            : settings.DisplayLines;
        NormalizeDiartSettings(settings);
    }

    private static int NormalizeLineCount(int lines)
    {
        return Math.Clamp(lines, 1, 8);
    }

    private static string NormalizeLegacyRemovedSpeechModels(string json)
    {
        try
        {
            var root = JsonNode.Parse(json)?.AsObject();
            if (root == null)
            {
                return json;
            }

            if (string.Equals(root["diarizationModel"]?.GetValue<string>(), "nemoSortformer", StringComparison.OrdinalIgnoreCase))
            {
                root["diarizationModel"] = "pyannoteCommunity";
            }

            if (string.Equals(root["asrEngine"]?.GetValue<string>(), "nemoSortformer", StringComparison.OrdinalIgnoreCase))
            {
                root["asrEngine"] = "none";
            }

            if (root["displayLines"] is { } legacyDisplayLines)
            {
                root["captionDisplayLines"] ??= legacyDisplayLines.DeepClone();
                root["overlayDisplayLines"] ??= legacyDisplayLines.DeepClone();
            }

            return root.ToJsonString(JsonOptions);
        }
        catch
        {
            return json;
        }
    }

    private static void NormalizeDiartSettings(AppSettings settings)
    {
        settings.DiartDurationSeconds = Math.Clamp(settings.DiartDurationSeconds, 3.0, 12.0);
        settings.DiartStepSeconds = Math.Clamp(settings.DiartStepSeconds, 0.25, 1.0);
        settings.DiartLatencySeconds = Math.Clamp(settings.DiartLatencySeconds, 0.5, 5.0);
        settings.DiartTauActive = Math.Clamp(settings.DiartTauActive, 0.3, 0.9);
        settings.DiartRhoUpdate = Math.Clamp(settings.DiartRhoUpdate, 0.0, 1.0);
        settings.DiartDeltaNew = Math.Clamp(settings.DiartDeltaNew, 0.3, 2.0);
    }
}
