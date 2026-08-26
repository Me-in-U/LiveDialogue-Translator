using System.IO;
using LiveDialogueTranslator.Core.Protocol;
using LiveDialogueTranslator.Core.Runtime;

namespace LiveDialogueTranslator.App.Services;

public sealed class AppPaths
{
    public AppPaths()
    {
        BaseDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LiveDialogue Translator");
        ModelDirectory = Path.Combine(BaseDirectory, "models");
        RuntimeDirectory = Path.Combine(BaseDirectory, "runtime");
        LogDirectory = Path.Combine(BaseDirectory, "logs");
        SettingsPath = Path.Combine(BaseDirectory, "settings.json");

        Directory.CreateDirectory(BaseDirectory);
        Directory.CreateDirectory(ModelDirectory);
        Directory.CreateDirectory(RuntimeDirectory);
        Directory.CreateDirectory(AsrRuntimeDirectory);
        Directory.CreateDirectory(SpeechSeparationRuntimeDirectory);
        Directory.CreateDirectory(LogDirectory);
    }

    public string BaseDirectory { get; }
    public string ModelDirectory { get; }
    public string RuntimeDirectory { get; }
    public string PythonDirectory => PythonRuntimeLayout.PythonDirectory(RuntimeDirectory);
    public string PythonExecutablePath => PythonRuntimeLayout.PythonExecutablePath(RuntimeDirectory);
    public string AsrRuntimeDirectory => Path.Combine(RuntimeDirectory, "asr-engines");
    public string SpeechSeparationRuntimeDirectory => Path.Combine(RuntimeDirectory, "speech-separation");
    public string LogDirectory { get; }
    public string SettingsPath { get; }

    public string WorkerScriptPath =>
        Path.Combine(AppContext.BaseDirectory, "worker", "speaker_worker.py");

    public string AsrPackageDirectory(AsrEngine engine)
    {
        return Path.Combine(AsrRuntimeDirectory, AsrEngineSlug(engine), "site");
    }

    public string SpeechSeparationPackageDirectory(SpeechSeparationModel model)
    {
        return Path.Combine(SpeechSeparationRuntimeDirectory, SpeechSeparationModelSlug(model), "site");
    }

    public static string SpeechSeparationModelSlug(SpeechSeparationModel model)
    {
        return model switch
        {
            SpeechSeparationModel.MossFormer2 => "mossformer2-ss-16k",
            SpeechSeparationModel.SepFormerWhamr16k => "sepformer-whamr16k",
            _ => "none"
        };
    }

    public static string AsrEngineSlug(AsrEngine engine)
    {
        return engine switch
        {
            AsrEngine.Qwen3Asr => "qwen3-asr",
            AsrEngine.WhisperLiveKitSortformer => "whisperlivekit-sortformer",
            AsrEngine.WhisperX => "whisperx",
            _ => "default"
        };
    }
}
