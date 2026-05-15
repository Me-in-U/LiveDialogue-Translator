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

    public WorkerConfiguration ToWorkerConfiguration(AppSettings settings)
    {
        var maxSpeakers = settings.ExactSpeakers is > 0 ? settings.ExactSpeakers.Value : settings.MaxSpeakers;
        return new WorkerConfiguration(
            settings.InputMode,
            settings.SttModel,
            settings.SttLanguages,
            settings.SttQualityPreset,
            settings.ComputeMode,
            settings.DiarizationEnabled,
            settings.DiarizationModel,
            maxSpeakers,
            null,
            settings.ShowLatency,
            new Dictionary<string, string>(),
            settings.DiartManualSettings,
            settings.DiartDurationSeconds,
            settings.DiartStepSeconds,
            settings.DiartLatencySeconds,
            settings.DiartTauActive,
            settings.DiartRhoUpdate,
            settings.DiartDeltaNew,
            settings.DiarizationQualityPreset,
            settings.AsrEngine);
    }

    private static void NormalizeSettings(AppSettings settings)
    {
        settings.SttQualityPreset = settings.SttQualityPreset >= 75 ? 100 : settings.SttQualityPreset >= 35 ? 50 : 0;
        settings.DiarizationQualityPreset = settings.DiarizationQualityPreset >= 75 ? 100 : settings.DiarizationQualityPreset >= 35 ? 50 : 0;
        if (settings.ExactSpeakers is > 0)
        {
            settings.MaxSpeakers = settings.ExactSpeakers.Value;
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
