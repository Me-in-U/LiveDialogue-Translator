using System.IO;
using LiveDialogueTranslator.Core.Protocol;

namespace LiveDialogueTranslator.App.Services;

public static class SpeechSeparationEnvironment
{
    public static void Apply(
        IDictionary<string, string?> environment,
        AppPaths paths,
        SpeechSeparationModel model)
    {
        environment["LIVE_DIALOGUE_TRANSLATOR_SPEECH_SEPARATION_MODEL"] =
            WorkerProtocol.FormatSpeechSeparationModel(model);

        if (model is SpeechSeparationModel.None or SpeechSeparationModel.Auto)
        {
            return;
        }

        var separationSite = paths.SpeechSeparationPackageDirectory(model);
        var existing = environment.TryGetValue("LIVE_DIALOGUE_TRANSLATOR_ASR_ENGINE_SITE", out var value)
            ? value
            : null;
        environment["LIVE_DIALOGUE_TRANSLATOR_ASR_ENGINE_SITE"] = string.IsNullOrWhiteSpace(existing)
            ? separationSite
            : string.Join(Path.PathSeparator, existing, separationSite);
    }

    public static string RequirementsPath(SpeechSeparationModel model)
    {
        var fileName = model switch
        {
            SpeechSeparationModel.MossFormer2 => "requirements-speech-separation-mossformer2.txt",
            SpeechSeparationModel.SepFormerWhamr16k => "requirements-speech-separation-sepformer.txt",
            _ => ""
        };

        if (fileName.Length == 0)
        {
            return "";
        }

        var packaged = Path.Combine(AppContext.BaseDirectory, "worker", fileName);
        return File.Exists(packaged)
            ? packaged
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "worker", fileName));
    }
}
