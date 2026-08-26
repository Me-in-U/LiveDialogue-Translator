using System.IO;
using LiveDialogueTranslator.Core.Protocol;

namespace LiveDialogueTranslator.App.Services;

public static class AsrEngineEnvironment
{
    public static void Apply(
        IDictionary<string, string?> environment,
        AppPaths paths,
        AsrEngine engine,
        DiarizationModel diarizationModel = DiarizationModel.PyannoteCommunity,
        bool diarizationEnabled = true)
    {
        ApplyEnvFile(environment, ResolveEnvPath(engine));
        foreach (var requiredEngine in RequiredAsrEngines(engine, diarizationModel, diarizationEnabled))
        {
            if (requiredEngine == engine)
            {
                continue;
            }

            ApplyEnvFile(environment, ResolveEnvPath(requiredEngine));
        }

        environment["LIVE_DIALOGUE_TRANSLATOR_ASR_ENGINE"] = WorkerProtocol.FormatAsrEngine(engine);

        var packageDirectories = OrderAsrPackageEngines(RequiredAsrEngines(engine, diarizationModel, diarizationEnabled))
            .Select(paths.AsrPackageDirectory)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (packageDirectories.Length == 0)
        {
            return;
        }

        environment["LIVE_DIALOGUE_TRANSLATOR_ASR_ENGINE_SITE"] = string.Join(Path.PathSeparator, packageDirectories);
    }

    public static IReadOnlyList<AsrEngine> RequiredAsrEngines(
        AsrEngine engine,
        DiarizationModel diarizationModel,
        bool diarizationEnabled = true)
    {
        var engines = new List<AsrEngine>();
        if (engine != AsrEngine.None)
        {
            engines.Add(engine);
        }

        if (diarizationEnabled && diarizationModel == DiarizationModel.Sortformer && !engines.Contains(AsrEngine.WhisperLiveKitSortformer))
        {
            engines.Add(AsrEngine.WhisperLiveKitSortformer);
        }

        return engines;
    }

    private static IEnumerable<AsrEngine> OrderAsrPackageEngines(IEnumerable<AsrEngine> engines)
    {
        return engines.OrderBy(engine => engine == AsrEngine.WhisperLiveKitSortformer ? 0 : 1);
    }

    public static string RequirementsPath(AsrEngine engine)
    {
        var fileName = engine switch
        {
            AsrEngine.Qwen3Asr => "requirements-qwen3-asr.txt",
            AsrEngine.WhisperLiveKitSortformer => "requirements-whisperlivekit-sortformer.txt",
            AsrEngine.WhisperX => "requirements-whisperx.txt",
            _ => ""
        };

        if (string.IsNullOrWhiteSpace(fileName))
        {
            return "";
        }

        var packaged = Path.Combine(AppContext.BaseDirectory, "worker", fileName);
        return File.Exists(packaged)
            ? packaged
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "worker", fileName));
    }

    private static string ResolveEnvPath(AsrEngine engine)
    {
        var fileName = engine switch
        {
            AsrEngine.Qwen3Asr => "qwen3-asr.env",
            AsrEngine.WhisperLiveKitSortformer => "whisperlivekit-sortformer.env",
            AsrEngine.WhisperX => "whisperx.env",
            _ => "default.env"
        };

        var packaged = Path.Combine(AppContext.BaseDirectory, "worker", "env", fileName);
        return File.Exists(packaged)
            ? packaged
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "worker", "env", fileName));
    }

    private static void ApplyEnvFile(IDictionary<string, string?> environment, string envPath)
    {
        if (!File.Exists(envPath))
        {
            return;
        }

        foreach (var rawLine in File.ReadLines(envPath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            string? value = line[(separator + 1)..].Trim().Trim('"');
            if (key.Length > 0)
            {
                environment[key] = value;
            }
        }
    }

}
