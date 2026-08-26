using LiveDialogueTranslator.Core.Protocol;

namespace LiveDialogueTranslator.App.Models;

public enum TranslateProvider
{
    Google,
    Google2,
    Ollama,
    OpenAI,
    OpenRouter,
    DeepL,
    Youdao,
    Baidu,
    MTranServer,
    LibreTranslate
}

public enum CaptionDisplayMode
{
    Original,
    Translated,
    Both
}

public sealed class AppSettings
{
    public InputMode InputMode { get; set; } = InputMode.SystemAndMic;
    public string SttModel { get; set; } = "small";
    public List<string> SttLanguages { get; set; } = [];
    public int SttQualityPreset { get; set; } = 100;
    public int DiarizationQualityPreset { get; set; } = 100;
    public ComputeMode ComputeMode { get; set; } = ComputeMode.Auto;
    public AsrEngine AsrEngine { get; set; } = AsrEngine.None;
    public SpeechSeparationModel SpeechSeparationModel { get; set; } = SpeechSeparationModel.Auto;
    public bool DiarizationEnabled { get; set; } = true;
    public DiarizationModel DiarizationModel { get; set; } = DiarizationModel.PyannoteCommunity;
    public bool DiartManualSettings { get; set; }
    public double DiartDurationSeconds { get; set; } = 5.0;
    public double DiartStepSeconds { get; set; } = 0.5;
    public double DiartLatencySeconds { get; set; } = 1.0;
    public double DiartTauActive { get; set; } = 0.555;
    public double DiartRhoUpdate { get; set; } = 0.422;
    public double DiartDeltaNew { get; set; } = 1.517;
    public TranslateProvider TranslateProvider { get; set; } = TranslateProvider.Google;
    public CaptionDisplayMode CaptionDisplayMode { get; set; } = CaptionDisplayMode.Both;
    public int MaxSpeakers { get; set; } = 4;
    public SpeakerCountMode SpeakerCountMode { get; set; } = SpeakerCountMode.ActiveMax;
    public int? ExactSpeakers { get; set; }
    public int DisplayLines { get; set; } = 3;
    public int CaptionDisplayLines { get; set; } = 3;
    public int OverlayDisplayLines { get; set; } = 3;
    public bool ShowLatency { get; set; } = true;
    public string TargetLanguage { get; set; } = "ko";
    public string? GoogleTranslateApiKey { get; set; }
    public bool Topmost { get; set; } = true;
    public bool OverlayOpen { get; set; }
    public OverlayWindowSettings Overlay { get; set; } = OverlayWindowSettings.Default();
    public string? HuggingFaceToken { get; set; }
}

public sealed class OverlayWindowSettings
{
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double Width { get; set; } = 650;
    public double Height { get; set; } = 135;
    public double FontSize { get; set; } = 12;
    public double Opacity { get; set; } = 1.0;
    public bool ClickThrough { get; set; }
    public bool ShowBorder { get; set; } = true;
    public bool AutoHeight { get; set; } = true;
    public string ActiveSpeakerColor { get; set; } = "#FF7DD3FC";
    public string InactiveSpeakerColor { get; set; } = "#CCFFFFFF";
    public string ActiveOriginalColor { get; set; } = "#FF8BD3FF";
    public string ActiveTranslationColor { get; set; } = "#FFFFD166";
    public string InactiveOriginalColor { get; set; } = "#FFFFFFFF";
    public string InactiveTranslationColor { get; set; } = "#FFEFE7A0";

    public static OverlayWindowSettings Default()
    {
        return new OverlayWindowSettings();
    }
}
