using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LiveDialogueTranslator.App.Models;
using LiveDialogueTranslator.App.Services;
using LiveDialogueTranslator.App.ViewModels;
using LiveDialogueTranslator.Core.Protocol;
using LiveDialogueTranslator.Core.Runtime;
using LiveDialogueTranslator.Core.Speakers;
using LiveDialogueTranslator.Core.Startup;
using LiveDialogueTranslator.Core.Transcripts;
using CoreInputMode = LiveDialogueTranslator.Core.Protocol.InputMode;

namespace LiveDialogueTranslator.App;

public partial class MainWindow : Window
{
    private const string DefaultWhisperModel = "large-v3-turbo";
    private const string DefaultQwenModel = "qwen3-asr-1.7b";
    private const string DefaultWhisperLiveKitModel = "default";
    private const double ConsoleAutoScrollThreshold = 2.0;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;
    private static readonly TimeSpan CaptionActiveBatchWindow = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan CaptionInactiveTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan CaptionFadeDuration = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan TranslationDebounceDelay = TimeSpan.FromMilliseconds(350);

    private readonly AppPaths paths = new();
    private readonly Localizer localizer = Localizer.FromWindows();
    private readonly SettingsStore settingsStore;
    private readonly AudioCaptureService audioCapture = new();
    private readonly WorkerClient workerClient;
    private readonly WorkerEnvironmentService workerEnvironment;
    private readonly HardwareDetectionService hardwareDetection = new();
    private readonly TranslationService translationService = new();
    private readonly SpeakerSegmentTimeline speakerTimeline = new();
    private readonly DispatcherTimer captionInactivityTimer;
    private readonly ObservableCollection<CaptionEntryViewModel> feed = [];
    private readonly ObservableCollection<OverlaySpeakerViewModel> captionSpeakers = [];
    private readonly HashSet<string> captionActiveSpeakerIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> consoleLines = [];

    private AppSettings settings = new();
    private SpeakerNameMap speakerNames = new();
    private CaptionMerger merger;
    private OverlayWindow? overlayWindow;
    private readonly SemaphoreSlim translationGate = new(1, 1);
    private readonly Dictionary<Guid, CancellationTokenSource> translationRequests = [];
    private readonly Dictionary<Guid, string> translationTexts = [];
    private readonly Dictionary<Guid, string> translationSourceTexts = [];
    private Guid? currentEntryId;
    private SetupActionKind detailActionKind = SetupActionKind.None;
    private string? lastSttUnavailableMessage;
    private AppPage activePage = AppPage.Captions;
    private string loadedSttModel = "-";
    private bool sttLoaded;
    private bool diarizationLoaded;
    private CancellationTokenSource? settingsApplyCts;
    private bool settingsApplyNeedsRestart;
    private bool applyingSettingsChange;
    private bool suppressSettingsChange = true;
    private bool adjustingPageHeight;
    private bool closingApp;
    private string pythonConsoleModelKey = string.Empty;
    private DateTime lastCaptionActiveSpeakerUpdateUtc = DateTime.MinValue;
    private DateTimeOffset translationRateLimitNoticeUntil = DateTimeOffset.MinValue;
    private HardwareProfile hardwareProfile = HardwareProfile.Unknown;
    private SpeechSeparationRecommendation speechSeparationRecommendation = new(
        SpeechSeparationModel.None,
        [],
        "Hardware detection has not completed.");
    private bool hardwareDetectionComplete;

    public MainWindow()
    {
        InitializeComponent();
        ApplyWindowIcon();
        ApplyLocalization();
        DataContext = this;

        settingsStore = new SettingsStore(paths.SettingsPath);
        settings = settingsStore.Load();
        pythonConsoleModelKey = BuildPythonConsoleModelKey(settings);
        workerClient = new WorkerClient(paths);
        workerEnvironment = new WorkerEnvironmentService(paths, localizer);
        captionInactivityTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        captionInactivityTimer.Tick += CaptionInactivityTimer_Tick;
        speakerNames = BuildSpeakerNameMap();
        merger = new CaptionMerger(MaxRetainedDisplayLines(), speakerNames);

        Feed = feed;
        CaptionSpeakerItems.ItemsSource = captionSpeakers;
        LoadSettingsIntoUi();
        ShowDefaultCaptionDetail();
        UpdateDebugStateText();

        Topmost = settings.Topmost;
        TopmostButton.Opacity = Topmost ? 1 : 0.45;

        audioCapture.ChunkCaptured += AudioCapture_ChunkCaptured;
        audioCapture.CaptureError += (_, message) => SetStatus($"Audio: {message}");
        workerClient.EventReceived += WorkerClient_EventReceived;
        workerClient.LogReceived += WorkerClient_LogReceived;
        workerEnvironment.ProgressChanged += WorkerEnvironment_ProgressChanged;
        workerEnvironment.LogReceived += WorkerEnvironment_LogReceived;
        captionInactivityTimer.Start();
        SourceInitialized += MainWindow_SourceInitialized;
        Loaded += MainWindow_Loaded;
    }

    public ObservableCollection<CaptionEntryViewModel> Feed { get; }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        ApplyRoundedWindowCorners();
    }

    private void ApplyRoundedWindowCorners()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var preference = DWMWCP_ROUND;
        _ = DwmSetWindowAttribute(
            handle,
            DWMWA_WINDOW_CORNER_PREFERENCE,
            ref preference,
            Marshal.SizeOf<int>());
    }

    private void ApplyWindowIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "LiveDialogueTranslator.ico");
        if (!File.Exists(iconPath))
        {
            return;
        }

        var icon = BitmapFrame.Create(new Uri(iconPath, UriKind.Absolute));
        Icon = icon;
        TitleIcon.Source = icon;
    }

    private void ApplyLocalization()
    {
        Title = L("AppTitle");
        TitleText.Text = L("AppTitle");
        StartButton.ToolTip = L("StartCapture");
        OverlayButton.ToolTip = L("OverlayWindow");
        TopmostButton.ToolTip = L("AlwaysOnTop");
        MinimizeButton.ToolTip = L("Minimize");
        CloseButton.ToolTip = L("Close");
        CaptionNavButton.ToolTip = L("Captions");
        SettingsNavButton.ToolTip = L("Settings");
        ConsoleNavButton.ToolTip = L("Console");
        InfoNavButton.ToolTip = L("Info");

        CurrentSpeakerText.Text = L("Original");
        CurrentOriginalText.Text = L("PressStart");
        CurrentTranslationText.Text = "";
        SetStatus(L("Ready"));
        DetailStatusText.Text = L("Ready");

        SettingsAudioGroupTitle.Text = L("SettingsAudioGroup");
        SettingsAsrGroupTitle.Text = L("SettingsAsrGroup");
        SettingsSpeakerProcessingGroupTitle.Text = L("SettingsSpeakerProcessingGroup");
        SettingsTranslationGroupTitle.Text = L("SettingsTranslationGroup");
        SettingsOutputGroupTitle.Text = L("SettingsOverlayGroup");
        SettingsToolsGroupTitle.Text = L("SettingsToolsGroup");
        SettingsAudioDescriptionText.Text = L("SettingsAudioDescription");
        SettingsAsrDescriptionText.Text = L("SettingsAsrDescription");
        SettingsSpeakerDescriptionText.Text = L("SettingsSpeakerDescription");
        SettingsTranslationDescriptionText.Text = L("SettingsTranslationDescription");
        SettingsOutputDescriptionText.Text = L("SettingsOutputDescription");
        SettingsToolsDescriptionText.Text = L("SettingsToolsDescription");
        AudioSourcesLabel.Text = L("AudioSources");
        InputSystemMicItem.Content = L("SystemMic");
        InputSystemOnlyItem.Content = L("SystemOnly");
        InputMixedDeviceItem.Content = L("MixedDevice");
        AudioInputSummaryText.Text = L("AudioInputSystemMicSummary");
        AsrEngineLabel.Text = L("AsrEngine");
        AsrEngineFasterWhisperItem.Content = L("FasterWhisper");
        AsrEngineQwenItem.Content = L("QwenAsr");
        AsrEngineWhisperLiveKitItem.Content = L("WhisperLiveKit");
        AsrEngineWhisperXItem.Content = L("WhisperX");
        AsrEngineHintText.Text = L("AsrEngineHint");
        SttDefaultItem.Content = DefaultWhisperLiveKitModel;
        SttModelLabel.Text = L("AsrModel");
        SttLanguagesLabel.Text = L("SttLanguages");
        SttPresetLabel.Text = L("SttPreset");
        SttPresetSpeedRadio.Content = L("Sensitive");
        SttPresetDebateRadio.Content = L("Balanced");
        SttPresetTalkShowRadio.Content = L("Stable");
        ComputeLabel.Text = L("Compute");
        ComputeAutoItem.Content = L("Auto");
        SpeakerProcessingModelLabel.Text = L("SpeakerProcessingModel");
        RedetectHardwareButton.Content = L("DetectHardwareAgain");
        HardwareSummaryText.Text = L("DetectingHardware");
        ModelManagerLabel.Text = L("ModelManager");
        ModelManagerButton.Content = L("Open");
        DebugLabel.Text = L("Debug");
        DebugButton.Content = L("Console");
        DebugButton.ToolTip = L("OpenConsole");
        ResetOverlayButton.Content = L("OverlayReset");
        ResetOverlayButton.ToolTip = L("OverlayResetTooltip");
        OverlayColorsButton.Content = L("OverlayColors");
        OverlayOpacityLabel.Text = L("OverlayOpacity");
        OverlayClickThroughCheck.Content = L("ClickThrough");
        OverlayClickThroughCheck.ToolTip = L("ClickThroughHelp");
        SpeakersLabel.Text = L("Speakers");
        SpeakerModeLabel.Text = L("SpeakerMode");
        SpeakerModeActiveMaxRadio.Content = L("SpeakerModeActiveMax");
        SpeakerModeExactRadio.Content = L("SpeakerModeExact");
        SpeakerCountModePanel.ToolTip = L("SpeakerModeHelp");
        SpeakerModeActiveMaxRadio.ToolTip = L("SpeakerModeHelp");
        SpeakerModeExactRadio.ToolTip = L("SpeakerModeHelp");
        CaptionDisplayLinesLabel.Text = L("CaptionDisplayLines");
        OverlayDisplayLinesLabel.Text = L("OverlayDisplayLines");
        DiarizationPresetLabel.Text = L("DiarizationPreset");
        DiarizationPresetSensitiveRadio.Content = L("Sensitive");
        DiarizationPresetBalancedRadio.Content = L("Balanced");
        DiarizationPresetStableRadio.Content = L("Stable");
        DiartManualCheck.Content = L("DiartManualTuning");
        DiartManualDescriptionText.Text = L("DiartManualDescription");
        DiartDurationLabel.Text = L("DiartDuration");
        DiartDurationLabel.ToolTip = L("DiartDurationHelp");
        DiartDurationBox.ToolTip = L("DiartDurationHelp");
        DiartStepLabel.Text = L("DiartStep");
        DiartStepLabel.ToolTip = L("DiartStepHelp");
        DiartStepBox.ToolTip = L("DiartStepHelp");
        DiartLatencyLabel.Text = L("DiartLatency");
        DiartLatencyLabel.ToolTip = L("DiartLatencyHelp");
        DiartLatencyBox.ToolTip = L("DiartLatencyHelp");
        DiartTauLabel.Text = L("DiartTauActive");
        DiartTauLabel.ToolTip = L("DiartTauActiveHelp");
        DiartTauBox.ToolTip = L("DiartTauActiveHelp");
        DiartRhoLabel.Text = L("DiartRhoUpdate");
        DiartRhoLabel.ToolTip = L("DiartRhoUpdateHelp");
        DiartRhoBox.ToolTip = L("DiartRhoUpdateHelp");
        DiartDeltaLabel.Text = L("DiartDeltaNew");
        DiartDeltaLabel.ToolTip = L("DiartDeltaNewHelp");
        DiartDeltaBox.ToolTip = L("DiartDeltaNewHelp");
        ModelDetailsGroupTitle.Text = L("ModelDetailsGroup");
        ModelDetailsDescriptionText.Text = L("ModelDetailsDescription");
        AutomaticSettingsTitleText.Text = L("AutomaticSettingsTitle");
        TranslationEnabledCheck.Content = L("TranslationEnabled");
        TranslationEnabledCheck.ToolTip = L("TranslationEnabledHelp");
        TranslateApiLabel.Text = L("TranslateApi");
        TargetLanguageLabel.Text = L("TargetLanguage");
        UpdateTranslationProviderAvailabilityText();
        SetUnavailableTranslationProviderItem(TranslateProviderGoogle2Item, "Google2");
        SetUnavailableTranslationProviderItem(TranslateProviderOllamaItem, "Ollama");
        SetUnavailableTranslationProviderItem(TranslateProviderOpenAIItem, "OpenAI");
        SetUnavailableTranslationProviderItem(TranslateProviderOpenRouterItem, "OpenRouter");
        SetUnavailableTranslationProviderItem(TranslateProviderDeepLItem, "DeepL");
        SetUnavailableTranslationProviderItem(TranslateProviderYoudaoItem, "Youdao");
        SetUnavailableTranslationProviderItem(TranslateProviderBaiduItem, "Baidu");
        SetUnavailableTranslationProviderItem(TranslateProviderMTranServerItem, "MTranServer");
        SetUnavailableTranslationProviderItem(TranslateProviderLibreTranslateItem, "LibreTranslate");
        TranslateApiSettingsButton.Content = L("ApiSetting");
        TranslateApiSettingsButton.ToolTip = L("ApiSetting");
        CaptionDisplayModeLabel.Text = L("CaptionDisplayMode");
        DisplayOriginalRadio.Content = L("DisplayOriginal");
        DisplayTranslatedRadio.Content = L("DisplayTranslated");
        DisplayBothRadio.Content = L("DisplayBoth");
        DetailActionButton.ToolTip = L("OpenTokenPage");
        ConsoleTitleText.Text = L("PythonConsole");
        ConsoleClearButton.Content = L("ClearConsoleLogs");
        ConsoleClearButton.ToolTip = L("ClearConsoleLogs");
        ConsoleKeepBottomButton.Content = L("KeepConsoleAtBottom");
        ConsoleKeepBottomButton.ToolTip = L("KeepConsoleAtBottom");

        ApplyInfoLocalization();
    }

    private void SetUnavailableTranslationProviderItem(ComboBoxItem item, string providerName)
    {
        item.Content = LF("TranslationProviderUnavailableItem", providerName);
        item.ToolTip = L("TranslationProviderUnavailable");
    }

    private void ApplyInfoLocalization()
    {
        InfoWelcomeText.Text = L("InfoWelcome");
        InfoLinksTitle.Text = L("InfoLinks");
        InfoProjectLabelRun.Text = $"{L("Project")}:";
        InfoReferenceLabelRun.Text = $"{L("ReferenceProject")}:";
        InfoAsrLabelRun.Text = $"{L("SupportedAsrBackends")}:";
        InfoDiarizationLabelRun.Text = $"{L("SupportedDiarizationBackends")}:";
        InfoSpeechSeparationLabelRun.Text = $"{L("SupportedSpeechSeparationBackends")}:";
        InfoLicenseLabelRun.Text = $"{L("License")}:";
        InfoRuntimeTitle.Text = L("Runtime");
        InfoVersionLabelRun.Text = $"{L("Version")}: ";
        InfoVersionRun.Text = AppVersionText();
        InfoRuntimeLabelRun.Text = $"{L("PythonRuntime")}: ";
        InfoRuntimeRun.Text = paths.PythonExecutablePath;
        OpenModelsFolderButton.Content = L("OpenModelsFolder");
        OpenRuntimeFolderButton.Content = L("OpenRuntimeFolder");
        InfoDataTitle.Text = L("Data");
        InfoStorageLabelRun.Text = $"{L("AppStorage")}: ";
        InfoStorageRun.Text = paths.BaseDirectory;
        InfoPrivacyText.Text = L("PrivacyNote");
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (audioCapture.IsRunning || workerClient.IsRunning)
        {
            await StopCaptureAsync(showStopped: true);
            return;
        }

        await StartCaptureAsync();
    }

    private async Task StopCaptureAsync(bool showStopped)
    {
        if (audioCapture.IsRunning || workerClient.IsRunning)
        {
            audioCapture.Stop();
            await workerClient.StopAsync();
            SetCaptureButtonRunning(false);
            HideSetupProgressIfReady();
        }

        if (!showStopped)
        {
            return;
        }

        CurrentSpeakerText.Text = L("Original");
        SetCurrentCaptionText(L("PressStart"), "");
        if (activePage == AppPage.Captions)
        {
            ShowDefaultCaptionDetail();
        }
        SetStatus(L("Stopped"));
    }

    private async Task StartCaptureAsync(bool showCaptionsPage = true)
    {
        SaveSettingsFromUi();
        await EnsureHardwareRecommendationAsync();
        SaveSettingsFromUi();
        var effectiveSpeechSeparationModel = EffectiveSpeechSeparationModel();
        speakerNames = BuildSpeakerNameMap();
        merger = new CaptionMerger(MaxRetainedDisplayLines(), speakerNames);
        speakerTimeline.Clear();
        captionSpeakers.Clear();
        captionActiveSpeakerIds.Clear();
        lastCaptionActiveSpeakerUpdateUtc = DateTime.MinValue;
        SetCaptionPlaceholderVisible(true);
        overlayWindow?.ClearSessionEntries();
        lastSttUnavailableMessage = null;
        ResetDebugState();

        StartButton.IsEnabled = false;
        if (showCaptionsPage)
        {
            ShowPage(AppPage.Captions);
        }
        CurrentSpeakerText.Text = L("Original");
        SetCurrentCaptionText(L("PreparingAudio"), "");
        ShowSetupProgress(L("CheckingSetupTitle"), L("PreparingEngine"), null);

        WorkerStartupPlan startupPlan;
        try
        {
            startupPlan = await workerEnvironment.EnsureReadyAsync(settings, effectiveSpeechSeparationModel);
        }
        catch (Exception ex)
        {
            StartButton.IsEnabled = true;
            SetCaptureButtonRunning(false);
            HideSetupProgressIfReady();
            ShowDetail(L("SetupFailed"), ex.Message, L("Error"), SetupActionHints.ForSetupFailure(ex.Message));
            SetStatus(L("SetupFailed"));
            return;
        }

        if (startupPlan.Capability == StartupCapability.Unavailable)
        {
            StartButton.IsEnabled = true;
            SetCaptureButtonRunning(false);
            HideSetupProgressIfReady();
            var warning = startupPlan.Warning ?? L("PythonUnavailable");
            ShowDetail(L("SetupUnavailable"), warning, L("Error"), SetupActionHints.ForSetupFailure(warning));
            SetStatus(L("SetupUnavailable"));
            return;
        }

        if (startupPlan.Capability == StartupCapability.NeedsHuggingFaceAccess)
        {
            StartButton.IsEnabled = true;
            SetCaptureButtonRunning(false);
            HideSetupProgressIfReady();
            var warning = startupPlan.Warning ?? L("LocalDiarizationNeedsAccess");
            CurrentSpeakerText.Text = L("Original");
            SetCurrentCaptionText(L("SetHfAccessCaption"), "");
            ShowDetail(L("LocalDiarizationSetup"), warning, L("ActionNeeded"), new SetupActionHint(SetupActionKind.HuggingFaceToken, "Set Access"));
            SetStatus(L("HfAccessNeeded"));
            await OpenHuggingFaceAccessAndRetryAsync();
            return;
        }

        var workerConfiguration = settingsStore.ToWorkerConfiguration(settings, effectiveSpeechSeparationModel);
        if (startupPlan.Capability == StartupCapability.SttOnly)
        {
            workerConfiguration = workerConfiguration with { DiarizationEnabled = false };
        }

        SetStatus(L("Starting"));
        try
        {
            await workerClient.StartAsync(workerConfiguration, settings.HuggingFaceToken);
        }
        catch (Exception ex)
        {
            StartButton.IsEnabled = true;
            SetCaptureButtonRunning(false);
            HideSetupProgressIfReady();
            ShowDetail(L("SetupFailed"), ex.Message, L("Error"), SetupActionHints.ForSetupFailure(ex.Message));
            SetStatus(L("SetupFailed"));
            return;
        }

        audioCapture.Start(
            settings.InputMode is CoreInputMode.SystemAndMic or CoreInputMode.SystemAudioOnly,
            settings.InputMode is CoreInputMode.SystemAndMic or CoreInputMode.MixedDevice);

        SetCaptureButtonRunning(true);
        StartButton.IsEnabled = true;
        if (startupPlan.Warning != null)
        {
            HideSetupProgressIfReady();
            ShowDetail(L("Details"), startupPlan.Warning, L("Warning"), SetupActionHints.ForWarning(startupPlan.Warning));
        }
        else
        {
            HideSetupProgressIfReady();
            ShowDefaultCaptionDetail();
        }
        CurrentSpeakerText.Text = L("Original");
        SetCurrentCaptionText(L("WaitingForSpeech"), "");
        SetStatus(L("Listening"));
    }

    private async void AudioCapture_ChunkCaptured(object? sender, AudioChunkEventArgs e)
    {
        await workerClient.SendAudioAsync(e.Source, e.TimestampMs, e.Pcm16Mono16Khz);
    }

    private void WorkerClient_EventReceived(object? sender, IWorkerEvent e)
    {
        Dispatcher.Invoke(() =>
        {
            switch (e)
            {
                case PartialCaptionEvent or FinalCaptionEvent:
                    ApplyCaptionEvent(e);
                    break;
                case SpeakerSegmentEvent segment:
                    speakerTimeline.Add(segment);
                    break;
                case ModelStatusEvent status:
                    UpdateModelDebugState(status);
                    SetStatus($"{status.Stage}: {status.Message}");
                    var actionHint = SetupActionHints.ForModelStatus(status.Stage, status.Message);
                    if (detailActionKind != SetupActionKind.None && actionHint.Kind == SetupActionKind.None)
                    {
                        break;
                    }

                    if (status.Stage.Equals("mock_mode", StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(lastSttUnavailableMessage))
                    {
                        ShowCaptionDetail(L("LocalSttUnavailable"), lastSttUnavailableMessage, L("Error"), actionHint);
                    }
                    else if (ShouldShowModelStatusInDetail(status.Stage))
                    {
                        ShowCaptionDetail(status.Stage, status.Message, null, actionHint);
                    }
                    break;
                case WorkerErrorEvent error:
                    UpdateModelDebugState(error);
                    SetStatus($"{error.Code}: {error.Message}");
                    if (error.Code.Equals("stt_unavailable", StringComparison.OrdinalIgnoreCase))
                    {
                        lastSttUnavailableMessage = error.Message;
                    }

                    if (IsDiarizationAccessFailure(error))
                    {
                        _ = HandleDiarizationAccessFailureAsync(error);
                        break;
                    }

                    if (!error.Recoverable)
                    {
                        _ = HandleFatalWorkerErrorAsync(error);
                        break;
                    }

                    ShowCaptionDetail(L("WorkerError"), $"{error.Code}: {error.Message}", error.Recoverable ? L("Warning") : L("Fatal"), SetupActionHints.ForWorkerError(error.Code, error.Message));
                    break;
                case LatencyEvent latency:
                    SetStatus($"{latency.Stage}: {latency.LatencyMs} ms");
                    break;
            }
        });
    }

    private void WorkerClient_LogReceived(object? sender, WorkerLogLine e)
    {
        Dispatcher.Invoke(() => AppendConsoleLine(e));
    }

    private void WorkerEnvironment_LogReceived(object? sender, WorkerLogLine e)
    {
        Dispatcher.Invoke(() => AppendConsoleLine(e));
    }

    private void WorkerEnvironment_ProgressChanged(object? sender, WorkerSetupProgress e)
    {
        Dispatcher.Invoke(() => ShowSetupProgress(e.Title, e.Detail, e.Percent));
    }

    private void AppendConsoleLine(WorkerLogLine line)
    {
        var shouldAutoScroll = IsConsoleKeepBottomEnabled() || IsConsoleScrolledToBottom();
        var previousOffset = ConsoleTextBox.VerticalOffset;
        var prefix = $"{DateTime.Now:HH:mm:ss.fff} {line.Stream}";
        consoleLines.Add($"{prefix} {line.Message}");
        if (consoleLines.Count > 1000)
        {
            consoleLines.RemoveRange(0, consoleLines.Count - 1000);
        }

        ConsoleTextBox.Text = string.Join(Environment.NewLine, consoleLines);
        if (shouldAutoScroll)
        {
            ConsoleTextBox.ScrollToEnd();
            return;
        }

        ConsoleTextBox.ScrollToVerticalOffset(previousOffset);
        ConsoleTextBox.Dispatcher.InvokeAsync(
            () => ConsoleTextBox.ScrollToVerticalOffset(previousOffset),
            DispatcherPriority.Background);
    }

    private void ClearPythonConsole()
    {
        consoleLines.Clear();
        ConsoleTextBox.Text = string.Empty;
        ConsoleTextBox.ScrollToHome();
    }

    private void ClearPythonConsoleIfModelChanged(string previousModelKey)
    {
        var currentModelKey = BuildPythonConsoleModelKey(settings);
        if (!string.IsNullOrEmpty(previousModelKey) &&
            !string.Equals(previousModelKey, currentModelKey, StringComparison.Ordinal))
        {
            ClearPythonConsole();
        }

        pythonConsoleModelKey = currentModelKey;
    }

    private static string BuildPythonConsoleModelKey(AppSettings settings)
    {
        return string.Join(
            "|",
            settings.AsrEngine,
            (settings.SttModel ?? string.Empty).Trim(),
            settings.DiarizationEnabled,
            settings.DiarizationModel,
            settings.SpeechSeparationModel);
    }

    private bool IsConsoleScrolledToBottom()
    {
        if (ConsoleTextBox.ExtentHeight <= 0 || ConsoleTextBox.ViewportHeight <= 0)
        {
            return true;
        }

        return ConsoleTextBox.VerticalOffset + ConsoleTextBox.ViewportHeight >= ConsoleTextBox.ExtentHeight - ConsoleAutoScrollThreshold;
    }

    private bool IsConsoleKeepBottomEnabled()
    {
        return ConsoleKeepBottomButton.IsChecked == true;
    }

    private void ResetDebugState()
    {
        loadedSttModel = "-";
        sttLoaded = false;
        diarizationLoaded = false;
        UpdateDebugStateText();
    }

    private void UpdateModelDebugState(ModelStatusEvent status)
    {
        if (status.Stage.Equals("configured", StringComparison.OrdinalIgnoreCase))
        {
            UpdateDebugStateText();
        }
        else if (status.Stage.Equals("stt_loaded", StringComparison.OrdinalIgnoreCase))
        {
            loadedSttModel = status.Message;
            sttLoaded = true;
        }
        else if (status.Stage.Equals("diarization_loaded", StringComparison.OrdinalIgnoreCase))
        {
            diarizationLoaded = true;
        }

        UpdateDebugStateText();
    }

    private void UpdateModelDebugState(WorkerErrorEvent error)
    {
        if (error.Code.Equals("stt_unavailable", StringComparison.OrdinalIgnoreCase))
        {
            sttLoaded = false;
        }
        else if (error.Code.Contains("diarization", StringComparison.OrdinalIgnoreCase) ||
                 error.Code.Equals("hf_access_denied", StringComparison.OrdinalIgnoreCase) ||
                 error.Code.Equals("hf_token_missing", StringComparison.OrdinalIgnoreCase))
        {
            diarizationLoaded = false;
        }

        UpdateDebugStateText();
    }

    private async Task HandleDiarizationAccessFailureAsync(WorkerErrorEvent error)
    {
        StartButton.IsEnabled = false;
        try
        {
            await StopCaptureAsync(showStopped: false);
        }
        finally
        {
            StartButton.IsEnabled = true;
        }

        SetCaptureButtonRunning(false);
        HideSetupProgressIfReady();
        CurrentSpeakerText.Text = L("Original");
        SetCurrentCaptionText(L("SetHfAccessCaption"), "");
        ShowCaptionDetail(
            L("LocalDiarizationSetup"),
            $"{error.Code}: {error.Message}",
            L("ActionNeeded"),
            SetupActionHints.ForWorkerError(error.Code, error.Message));
        SetStatus(L("HfAccessNeeded"));
    }

    private async Task HandleFatalWorkerErrorAsync(WorkerErrorEvent error)
    {
        StartButton.IsEnabled = false;
        try
        {
            await StopCaptureAsync(showStopped: false);
        }
        finally
        {
            StartButton.IsEnabled = true;
        }

        SetCaptureButtonRunning(false);
        HideSetupProgressIfReady();
        ShowCaptionDetail(
            L("WorkerError"),
            $"{error.Code}: {error.Message}",
            L("Fatal"),
            SetupActionHints.ForWorkerError(error.Code, error.Message));
    }

    private static bool IsDiarizationAccessFailure(WorkerErrorEvent error)
    {
        return error.Code.Equals("hf_access_denied", StringComparison.OrdinalIgnoreCase) ||
               error.Code.Equals("hf_token_missing", StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateDebugStateText()
    {
        DebugStateText.Text = LF("ModelLine", LF("DebugState", loadedSttModel, Mark(sttLoaded), Mark(diarizationLoaded), SpeakerDebugText()));
    }

    private static string Mark(bool value)
    {
        return value ? "O" : "X";
    }

    private string SpeakerDebugText()
    {
        return settings.SpeakerCountMode == SpeakerCountMode.Exact
            ? LF("SpeakerExactDebug", settings.ExactSpeakers ?? settings.MaxSpeakers)
            : LF("SpeakerAutoDebug", settings.MaxSpeakers);
    }

    private static int CompareSpeakers(string left, string right)
    {
        var leftKey = SpeakerSortKey(left);
        var rightKey = SpeakerSortKey(right);
        var keyCompare = leftKey.CompareTo(rightKey);
        return keyCompare != 0 ? keyCompare : string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static int SpeakerSortKey(string speakerId)
    {
        if (speakerId.Equals("mic", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        const string prefix = "speaker_";
        if (speakerId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(speakerId[prefix.Length..], out var number))
        {
            return number;
        }

        return int.MaxValue;
    }

    private static bool ShouldShowModelStatusInDetail(string stage)
    {
        return !stage.Equals("configured", StringComparison.OrdinalIgnoreCase) &&
               !stage.Equals("stt_loaded", StringComparison.OrdinalIgnoreCase) &&
               !stage.Equals("diarization_loaded", StringComparison.OrdinalIgnoreCase) &&
               !stage.Equals("listening", StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyCaptionEvent(IWorkerEvent e)
    {
        var entry = merger.Apply(AlignCaptionSpeaker(e));
        if (entry == null)
        {
            return;
        }

        StartTranslationForEntryAsync(entry);
        var entryViewModel = new CaptionEntryViewModel(entry, TranslationFor(entry), settings.CaptionDisplayMode);
        RenderFeed(refreshCaptionSpeakers: false);
        UpdateCaptionSpeaker(entryViewModel, refreshActivity: true);
        overlayWindow?.UpdateEntry(entryViewModel, settings.OverlayDisplayLines);

        UpdateCurrentCaption(entry);
        if (settings.ShowLatency && entry.LatencyMs.HasValue)
        {
            SetStatus($"{entry.LatencyMs.Value} ms");
        }
    }

    private IWorkerEvent AlignCaptionSpeaker(IWorkerEvent e)
    {
        return e switch
        {
            PartialCaptionEvent partial => partial with
            {
                SpeakerId = speakerTimeline.ResolveSpeaker(partial.StartMs, partial.EndMs, partial.SpeakerId)
            },
            FinalCaptionEvent final => final with
            {
                SpeakerId = speakerTimeline.ResolveSpeaker(final.StartMs, final.EndMs, final.SpeakerId)
            },
            _ => e
        };
    }

    private void RenderFeed(bool refreshCaptionSpeakers = true)
    {
        var entries = LiveEntryViewModels();
        feed.Clear();
        foreach (var entry in entries)
        {
            feed.Add(entry);
        }

        if (refreshCaptionSpeakers)
        {
            RefreshCaptionSpeakers(entries, refreshActivity: false);
        }
    }

    private IReadOnlyList<CaptionEntryViewModel> LiveEntryViewModels()
    {
        return merger.Entries
            .Select(entry => new CaptionEntryViewModel(entry, TranslationFor(entry), settings.CaptionDisplayMode))
            .ToArray();
    }

    private void RefreshCaptionSpeakers(IEnumerable<CaptionEntryViewModel> entries, bool refreshActivity)
    {
        var orderedEntries = entries.OrderBy(entry => entry.StartMs).ToArray();
        var speakerIds = orderedEntries.Select(entry => entry.SpeakerId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var speaker in captionSpeakers.ToArray())
        {
            if (!speakerIds.Contains(speaker.SpeakerId))
            {
                captionActiveSpeakerIds.Remove(speaker.SpeakerId);
                captionSpeakers.Remove(speaker);
            }
        }

        foreach (var entry in orderedEntries)
        {
            UpdateCaptionSpeaker(entry, refreshActivity);
        }

        SetCaptionPlaceholderVisible(captionSpeakers.Count == 0);
    }

    private void UpdateCaptionSpeaker(CaptionEntryViewModel entry, bool refreshActivity)
    {
        var speaker = captionSpeakers.FirstOrDefault(candidate => candidate.SpeakerId == entry.SpeakerId);
        if (speaker == null)
        {
            speaker = new OverlaySpeakerViewModel(entry, settings.CaptionDisplayLines, settings.Overlay);
            InsertCaptionSpeakerSorted(speaker);
        }
        else
        {
            speaker.Apply(entry, settings.CaptionDisplayLines, refreshActivity);
        }

        if (refreshActivity)
        {
            MarkCaptionActiveSpeaker(speaker);
        }

        SetCaptionPlaceholderVisible(captionSpeakers.Count == 0);
    }

    private void MarkCaptionActiveSpeaker(OverlaySpeakerViewModel activeSpeaker)
    {
        var now = DateTime.UtcNow;
        if (lastCaptionActiveSpeakerUpdateUtc == DateTime.MinValue ||
            now - lastCaptionActiveSpeakerUpdateUtc > CaptionActiveBatchWindow)
        {
            captionActiveSpeakerIds.Clear();
        }

        lastCaptionActiveSpeakerUpdateUtc = now;
        captionActiveSpeakerIds.Add(activeSpeaker.SpeakerId);
        ApplyCaptionActiveSpeakers();
    }

    private void ApplyCaptionActiveSpeakers()
    {
        foreach (var speaker in captionSpeakers)
        {
            speaker.SetCurrent(captionActiveSpeakerIds.Contains(speaker.SpeakerId));
        }
    }

    private void CaptionInactivityTimer_Tick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var removed = false;
        foreach (var speaker in captionSpeakers.ToArray())
        {
            if (!speaker.IsFading && now - speaker.LastUpdatedUtc > CaptionInactiveTimeout)
            {
                captionActiveSpeakerIds.Remove(speaker.SpeakerId);
                speaker.SetCurrent(false);
                speaker.BeginFade(now);
            }
            else if (speaker.IsFading &&
                     speaker.FadeStartedUtc.HasValue &&
                     now - speaker.FadeStartedUtc.Value > CaptionFadeDuration)
            {
                captionActiveSpeakerIds.Remove(speaker.SpeakerId);
                speaker.ClearLinesAfterFade();
                captionSpeakers.Remove(speaker);
                removed = true;
            }
        }

        if (removed)
        {
            SetCaptionPlaceholderVisible(captionSpeakers.Count == 0);
        }
    }

    private void ApplyCaptionSpeakerColors()
    {
        foreach (var speaker in captionSpeakers)
        {
            speaker.ApplyColors(settings.Overlay);
        }
    }

    private void InsertCaptionSpeakerSorted(OverlaySpeakerViewModel speaker)
    {
        var index = 0;
        while (index < captionSpeakers.Count && CompareSpeakers(captionSpeakers[index].SpeakerId, speaker.SpeakerId) <= 0)
        {
            index++;
        }

        captionSpeakers.Insert(index, speaker);
    }

    private string TranslationFor(CaptionEntry entry)
    {
        if (!settings.TranslationEnabled)
        {
            return "";
        }

        return translationTexts.ContainsKey(entry.Id) ? translationTexts[entry.Id] : "";
    }

    private void UpdateCurrentCaption(CaptionEntry entry)
    {
        currentEntryId = entry.Id;
        CurrentSpeakerText.Text = entry.SpeakerName;
        SetCurrentCaptionText(entry.Text, TranslationFor(entry));
        if (activePage == AppPage.Captions)
        {
            ShowCaptionEntryDetail(entry);
        }
    }

    private void SetCurrentCaptionText(string original, string translated)
    {
        var hasTranslation = HasUsefulTranslation(original, translated);
        CurrentOriginalText.Text = original;
        CurrentTranslationText.Text = hasTranslation ? translated : "";
        CurrentOriginalText.Visibility = Visibility.Visible;

        switch (settings.CaptionDisplayMode)
        {
            case CaptionDisplayMode.Translated:
                CurrentOriginalText.Text = hasTranslation ? translated : original;
                CurrentTranslationText.Visibility = Visibility.Collapsed;
                break;
            case CaptionDisplayMode.Both:
                CurrentOriginalText.Visibility = Visibility.Visible;
                CurrentTranslationText.Visibility = hasTranslation ? Visibility.Visible : Visibility.Collapsed;
                break;
            default:
                CurrentOriginalText.Visibility = Visibility.Visible;
                CurrentTranslationText.Visibility = Visibility.Collapsed;
                break;
        }
    }

    private void SetCaptionPlaceholder(string title, string original, string translated = "")
    {
        CurrentSpeakerText.Text = title;
        SetCurrentCaptionText(original, translated);
        SetCaptionPlaceholderVisible(captionSpeakers.Count == 0);
    }

    private void SetCaptionPlaceholderVisible(bool visible)
    {
        CaptionSpeakerItems.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        CaptionPlaceholderPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowCaptionEntryDetail(CaptionEntry entry)
    {
        ShowDetail(entry.SpeakerName, FormatCaptionDisplayText(entry), null);
    }

    private string FormatCaptionDisplayText(CaptionEntry entry)
    {
        var translated = TranslationFor(entry);
        var hasTranslation = HasUsefulTranslation(entry.Text, translated);
        return settings.CaptionDisplayMode switch
        {
            CaptionDisplayMode.Translated => hasTranslation ? translated : entry.Text,
            CaptionDisplayMode.Both when hasTranslation => $"{entry.Text}{Environment.NewLine}{Environment.NewLine}{translated}",
            _ => entry.Text
        };
    }

    private static bool HasUsefulTranslation(string original, string translated)
    {
        return !string.IsNullOrWhiteSpace(translated) && !IsDuplicateTranslation(original, translated);
    }

    private static bool IsDuplicateTranslation(string original, string translated)
    {
        if (string.IsNullOrWhiteSpace(original) || string.IsNullOrWhiteSpace(translated))
        {
            return false;
        }

        return string.Equals(
            NormalizeComparableText(original),
            NormalizeComparableText(translated),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeComparableText(string value)
    {
        return string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private void StartTranslationForEntryAsync(CaptionEntry entry)
    {
        if (!settings.TranslationEnabled || settings.CaptionDisplayMode == CaptionDisplayMode.Original)
        {
            return;
        }

        if (!entry.IsFinal)
        {
            return;
        }

        if (translationSourceTexts.TryGetValue(entry.Id, out var sourceText) &&
            string.Equals(sourceText, entry.Text, StringComparison.Ordinal) &&
            translationTexts.ContainsKey(entry.Id))
        {
            return;
        }

        translationSourceTexts[entry.Id] = entry.Text;
        if (translationRequests.Remove(entry.Id, out var previous))
        {
            previous.Cancel();
        }

        var cts = new CancellationTokenSource();
        translationRequests[entry.Id] = cts;
        _ = TranslateEntryAsync(entry.Id, entry.Text, settings.TranslateProvider, settings.TargetLanguage, cts);
    }

    private async Task TranslateEntryAsync(Guid entryId, string text, TranslateProvider provider, string targetLanguage, CancellationTokenSource cts)
    {
        try
        {
            // Google's public translate endpoint is unofficial and throttles bursts.
            // Wait briefly for final-caption merges to settle, then send one request
            // at a time so partial updates cannot flood the endpoint.
            await Task.Delay(TranslationDebounceDelay, cts.Token);
            string translated;
            await translationGate.WaitAsync(cts.Token);
            try
            {
                translated = await translationService.TranslateAsync(
                    text,
                    targetLanguage,
                    provider,
                    settings.GoogleTranslateApiKey,
                    cts.Token);
            }
            finally
            {
                translationGate.Release();
            }

            if (cts.IsCancellationRequested || string.IsNullOrWhiteSpace(translated))
            {
                return;
            }

            Dispatcher.Invoke(() =>
            {
                if (cts.IsCancellationRequested)
                {
                    return;
                }

                translationRateLimitNoticeUntil = DateTimeOffset.MinValue;
                translationTexts[entryId] = translated;
                translationRequests.Remove(entryId);
                var entry = merger.Entries.FirstOrDefault(candidate => candidate.Id == entryId);
                if (entry == null)
                {
                    return;
                }

                if (currentEntryId == entryId)
                {
                    UpdateCurrentCaption(entry);
                }

                RenderFeed();

                overlayWindow?.UpdateEntries(LiveEntryViewModels(), settings.OverlayDisplayLines);
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (TranslationRateLimitException ex)
        {
            Dispatcher.Invoke(() =>
            {
                var now = DateTimeOffset.UtcNow;
                if (now < translationRateLimitNoticeUntil)
                {
                    return;
                }

                translationRateLimitNoticeUntil = now + ex.RetryAfter;
                var retryMinutes = Math.Max(1, (int)Math.Ceiling(ex.RetryAfter.TotalMinutes));
                var message = LF("TranslationRateLimited", retryMinutes);
                AppendConsoleLine(new WorkerLogLine("translation", message));
                if (activePage == AppPage.Captions)
                {
                    ShowCaptionDetail(L("Translation"), message, L("Warning"));
                }
            });
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() =>
            {
                AppendConsoleLine(new WorkerLogLine("translation", LF("TranslationUnavailable", ex.Message)));
                if (activePage == AppPage.Captions)
                {
                    ShowCaptionDetail(L("Translation"), LF("TranslationUnavailable", ex.Message), L("Warning"));
                }
            });
        }
        finally
        {
            Dispatcher.Invoke(() =>
            {
                if (translationRequests.TryGetValue(entryId, out var active) && ReferenceEquals(active, cts))
                {
                    translationRequests.Remove(entryId);
                }
            });
            cts.Dispose();
        }
    }

    private void CaptionNavButton_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(AppPage.Captions);
    }

    private void SettingsNavButton_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(AppPage.Settings);
    }

    private void InfoNavButton_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(AppPage.Info);
    }

    private void ConsoleNavButton_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(AppPage.Console);
    }

    private void DebugButton_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(AppPage.Console);
    }

    private void ConsoleClearButton_Click(object sender, RoutedEventArgs e)
    {
        ClearPythonConsole();
    }

    private void ConsoleKeepBottomButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsConsoleKeepBottomEnabled())
        {
            ConsoleTextBox.ScrollToEnd();
        }
    }

    private void SttLanguagesButton_Click(object sender, RoutedEventArgs e)
    {
        SaveSettingsFromUi();
        var window = new SttLanguageWindow(settings.SttLanguages, localizer)
        {
            Owner = this
        };

        if (window.ShowDialog() != true)
        {
            return;
        }

        settings.SttLanguages = window.SelectedLanguages;
        settingsStore.Save(settings);
        UpdateSttLanguagesButton();
        _ = QueueSettingsApplyAsync(restartIfRunning: true);
    }

    private void RestartSetting_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressSettingsChange)
        {
            return;
        }

        ApplyAsrEngineUiState(normalizeSelection: true);
        if (sender == SpeakerProcessingModelBox)
        {
            ApplySpeakerProcessingChoiceToState();
        }
        if (sender == InputModeBox)
        {
            UpdateAudioInputUiState();
        }
        if (sender == ComputeModeBox || sender == AsrEngineBox || sender == SttModelBox || sender == SpeakerProcessingModelBox)
        {
            UpdateSpeechSeparationRecommendation(normalizeSelection: true);
        }
        else
        {
            UpdateSpeakerProcessingUiState();
        }
        UpdateDiartManualControls();
        UpdateSttPresetSummary();
        _ = QueueSettingsApplyAsync(restartIfRunning: true);
    }

    private void RestartSetting_CheckedChanged(object sender, RoutedEventArgs e)
    {
        if (suppressSettingsChange)
        {
            return;
        }

        NormalizeDiarizationForSpeakerCount();
        ApplyAsrEngineUiState(normalizeSelection: true);
        UpdateSpeakerProcessingUiState();
        UpdateDiartManualControls();
        UpdateSttPresetSummary();
        _ = QueueSettingsApplyAsync(restartIfRunning: true);
    }

    private void RestartSetting_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (suppressSettingsChange)
        {
            return;
        }

        UpdateSttPresetSummary();
        UpdateSpeakerProcessingUiState();
        _ = QueueSettingsApplyAsync(restartIfRunning: true);
    }

    private void RestartSetting_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (suppressSettingsChange)
        {
            return;
        }

        UpdateSttPresetSummary();
        UpdateSpeakerProcessingUiState();
        _ = QueueSettingsApplyAsync(restartIfRunning: true);
    }

    private void ImmediateSetting_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _ = QueueSettingsApplyAsync(restartIfRunning: false);
    }

    private void TranslationSetting_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyTranslationSettingsImmediately();
    }

    private void TranslationEnabled_CheckedChanged(object sender, RoutedEventArgs e)
    {
        if (suppressSettingsChange)
        {
            return;
        }

        UpdateTranslationUiState();
        ApplyTranslationSettingsImmediately();
    }

    private void ApplyTranslationSettingsImmediately()
    {
        if (suppressSettingsChange)
        {
            return;
        }

        SaveSettingsFromUi();
        ApplyDisplaySettings();
        if (!audioCapture.IsRunning && !workerClient.IsRunning)
        {
            SetStatus(L("SettingsSaved"));
        }
    }

    private void ImmediateSetting_CheckedChanged(object sender, RoutedEventArgs e)
    {
        _ = QueueSettingsApplyAsync(restartIfRunning: false);
    }

    private void ImmediateSetting_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _ = QueueSettingsApplyAsync(restartIfRunning: false);
    }

    private void OverlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (overlayWindow == null || !overlayWindow.IsVisible)
        {
            ShowOverlay(rememberOpen: true);
            return;
        }

        CloseOverlay(rememberClosed: true);
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await EnsureHardwareRecommendationAsync();
        if (settings.OverlayOpen)
        {
            ShowOverlay(rememberOpen: false);
        }
    }

    private async void RedetectHardwareButton_Click(object sender, RoutedEventArgs e)
    {
        await EnsureHardwareRecommendationAsync(force: true);
        SaveSettingsFromUi();
        AdjustWindowHeightToSettingsContent();
    }

    private async Task EnsureHardwareRecommendationAsync(bool force = false)
    {
        if (hardwareDetectionComplete && !force)
        {
            UpdateSpeechSeparationRecommendation(normalizeSelection: true);
            return;
        }

        RedetectHardwareButton.IsEnabled = false;
        HardwareSummaryText.Text = L("DetectingHardware");
        try
        {
            hardwareProfile = await hardwareDetection.DetectAsync();
            hardwareDetectionComplete = true;
            UpdateSpeechSeparationRecommendation(normalizeSelection: true);
        }
        finally
        {
            RedetectHardwareButton.IsEnabled = true;
        }
    }

    private void UpdateAudioInputUiState()
    {
        var inputMode = ParseSelectedTag(InputModeBox, settings.InputMode);
        AudioInputSummaryText.Text = inputMode switch
        {
            CoreInputMode.SystemAudioOnly => L("AudioInputSystemOnlySummary"),
            CoreInputMode.MixedDevice => L("AudioInputMixedDeviceSummary"),
            _ => L("AudioInputSystemMicSummary")
        };
    }

    private void UpdateSpeechSeparationRecommendation(bool normalizeSelection)
    {
        if (!hardwareDetectionComplete)
        {
            HardwareSummaryText.Text = L("DetectingHardware");
            return;
        }

        var computeMode = ParseSelectedTag(ComputeModeBox, settings.ComputeMode);
        var asrEngine = ParseSelectedTag(AsrEngineBox, settings.AsrEngine);
        var sttModel = SelectedContent(SttModelBox, settings.SttModel);
        var requested = ParseSelectedTag(SpeechSeparationModelBox, settings.SpeechSeparationModel);
        speechSeparationRecommendation = SpeechSeparationAdvisor.Recommend(
            hardwareProfile,
            computeMode,
            asrEngine,
            sttModel);

        if (normalizeSelection &&
            requested is not SpeechSeparationModel.Auto and not SpeechSeparationModel.None &&
            !speechSeparationRecommendation.SupportedModels.Contains(requested))
        {
            requested = SpeechSeparationModel.Auto;
        }

        PopulateSpeechSeparationModelItems(requested, computeMode, asrEngine, sttModel);
        PopulateSpeakerProcessingModelItems(SpeakerProcessingChoiceFromState(), computeMode, asrEngine, sttModel);
        UpdateSpeakerProcessingUiState(requested);
        var gpu = hardwareProfile.HasNvidiaGpu
            ? LF("HardwareGpuSummary", hardwareProfile.GpuName ?? "NVIDIA GPU", hardwareProfile.GpuMemoryGiB)
            : L("HardwareNoSupportedGpu");
        HardwareSummaryText.Text = LF(
            "HardwareSummary",
            hardwareProfile.CpuName,
            hardwareProfile.LogicalProcessorCount,
            hardwareProfile.MemoryGiB,
            gpu);

        if (speechSeparationRecommendation.IsAvailable)
        {
            SpeechSeparationRecommendationText.Text = LF(
                "SpeechSeparationRecommended",
                SpeechSeparationAdvisor.DisplayName(speechSeparationRecommendation.Model),
                LocalizedSpeechSeparationReason(computeMode, asrEngine));
        }
        else
        {
            SpeechSeparationRecommendationText.Text = LF(
                "SpeechSeparationUnavailable",
                LocalizedSpeechSeparationReason(computeMode, asrEngine));
        }

        UpdateDiartManualControls();
        UpdateSttPresetSummary();
    }

    private void PopulateSpeechSeparationModelItems(
        SpeechSeparationModel requested,
        ComputeMode computeMode,
        AsrEngine asrEngine,
        string? sttModel)
    {
        var previousSuppression = suppressSettingsChange;
        suppressSettingsChange = true;
        try
        {
            SpeechSeparationModelBox.Items.Clear();
            var automaticModel = speechSeparationRecommendation.IsAvailable
                ? CompactSpeechSeparationDisplayName(speechSeparationRecommendation.Model)
                : L("SpeechSeparationOff");
            AddSpeechSeparationItem(
                LF("SpeechSeparationAutoWithModel", automaticModel),
                SpeechSeparationModel.Auto,
                tooltip: speechSeparationRecommendation.IsAvailable
                    ? LF(
                        "SpeechSeparationEffectiveAuto",
                        SpeechSeparationAdvisor.DisplayName(speechSeparationRecommendation.Model))
                    : L("SpeechSeparationEffectiveOff"));
            AddSpeechSeparationItem(L("SpeechSeparationOff"), SpeechSeparationModel.None);
            foreach (var option in SpeechSeparationAdvisor.Catalog)
            {
                var assessment = SpeechSeparationAdvisor.Assess(
                    hardwareProfile,
                    computeMode,
                    asrEngine,
                    option.Model,
                    sttModel);
                AddSpeechSeparationItem(
                    assessment.IsSupported
                        ? option.DisplayName
                        : LF("SpeechSeparationUnavailableItem", option.DisplayName),
                    option.Model,
                    assessment.IsSupported,
                    LocalizedSpeechSeparationAssessment(assessment));
            }

            var selectable = requested is SpeechSeparationModel.Auto or SpeechSeparationModel.None ||
                speechSeparationRecommendation.SupportedModels.Contains(requested);
            SelectByTag(
                SpeechSeparationModelBox,
                (selectable ? requested : SpeechSeparationModel.Auto).ToString());
        }
        finally
        {
            suppressSettingsChange = previousSuppression;
        }
    }

    private void AddSpeechSeparationItem(
        string content,
        SpeechSeparationModel model,
        bool isEnabled = true,
        string? tooltip = null)
    {
        var item = new ComboBoxItem
        {
            Content = content,
            Tag = model.ToString(),
            IsEnabled = isEnabled,
            ToolTip = tooltip
        };
        ToolTipService.SetShowOnDisabled(item, true);
        SpeechSeparationModelBox.Items.Add(item);
    }

    private void PopulateSpeakerProcessingModelItems(
        string requestedChoice,
        ComputeMode computeMode,
        AsrEngine asrEngine,
        string? sttModel)
    {
        var previousSuppression = suppressSettingsChange;
        suppressSettingsChange = true;
        try
        {
            SpeakerProcessingModelBox.Items.Clear();
            AddSpeakerProcessingItem(
                LF("SpeakerProcessingAutoWithModel", AutomaticSpeakerProcessingModelName()),
                "Auto",
                tooltip: L("SpeakerProcessingAutoHelp"));
            AddSpeakerProcessingItem(L("SpeechSeparationOff"), "Off", tooltip: L("SpeakerModelOffDescription"));

            foreach (var option in SpeechSeparationAdvisor.Catalog)
            {
                var assessment = SpeechSeparationAdvisor.Assess(
                    hardwareProfile,
                    computeMode,
                    asrEngine,
                    option.Model,
                    sttModel);
                AddSpeakerProcessingItem(
                    assessment.IsSupported
                        ? option.DisplayName
                        : LF("SpeechSeparationUnavailableItem", option.DisplayName),
                    option.Model.ToString(),
                    assessment.IsSupported,
                    LocalizedSpeechSeparationAssessment(assessment));
            }

            AddSpeakerProcessingItem(L("PyannoteCommunity"), "PyannoteCommunity", tooltip: L("SpeakerModelCommunityDescription"));
            AddSpeakerProcessingItem(L("DiartRealtime"), "Diart", tooltip: L("SpeakerModelDiartDescription"));
            AddSpeakerProcessingItem(L("Sortformer"), "Sortformer", tooltip: L("SpeakerModelSortformerDescription"));
            SelectByTag(SpeakerProcessingModelBox, requestedChoice);
            if (SpeakerProcessingModelBox.SelectedItem is null)
            {
                SelectByTag(SpeakerProcessingModelBox, "Auto");
            }
        }
        finally
        {
            suppressSettingsChange = previousSuppression;
        }
    }

    private void AddSpeakerProcessingItem(
        string content,
        string tag,
        bool isEnabled = true,
        string? tooltip = null)
    {
        var item = new ComboBoxItem
        {
            Content = content,
            Tag = tag,
            IsEnabled = isEnabled,
            ToolTip = tooltip
        };
        ToolTipService.SetShowOnDisabled(item, true);
        SpeakerProcessingModelBox.Items.Add(item);
    }

    private string SpeakerProcessingChoiceFromState()
    {
        var separation = ParseSelectedTag(SpeechSeparationModelBox, settings.SpeechSeparationModel);
        if (separation == SpeechSeparationModel.Auto)
        {
            return "Auto";
        }

        if (separation == SpeechSeparationModel.MossFormer2)
        {
            return "MossFormer2";
        }

        if (separation == SpeechSeparationModel.SepFormerWhamr16k)
        {
            return "SepFormerWhamr16k";
        }

        if (DiarizationCheck.IsChecked != true)
        {
            return "Off";
        }

        return SelectedDiarizationModel().ToString();
    }

    private string SelectedSpeakerProcessingChoice()
    {
        return (SpeakerProcessingModelBox.SelectedItem as ComboBoxItem)?.Tag?.ToString()
            ?? SpeakerProcessingChoiceFromState();
    }

    private void ApplySpeakerProcessingChoiceToState()
    {
        var choice = SelectedSpeakerProcessingChoice();
        var previousSuppression = suppressSettingsChange;
        suppressSettingsChange = true;
        try
        {
            switch (choice)
            {
                case "Auto":
                    SelectByTag(SpeechSeparationModelBox, SpeechSeparationModel.Auto.ToString());
                    DiarizationCheck.IsChecked = true;
                    SelectDiarizationModel(AutomaticDiarizationModel());
                    break;
                case "MossFormer2":
                    SelectByTag(SpeechSeparationModelBox, SpeechSeparationModel.MossFormer2.ToString());
                    DiarizationCheck.IsChecked = false;
                    break;
                case "SepFormerWhamr16k":
                    SelectByTag(SpeechSeparationModelBox, SpeechSeparationModel.SepFormerWhamr16k.ToString());
                    DiarizationCheck.IsChecked = false;
                    break;
                case "PyannoteCommunity":
                    SelectByTag(SpeechSeparationModelBox, SpeechSeparationModel.None.ToString());
                    DiarizationCheck.IsChecked = true;
                    SelectDiarizationModel(DiarizationModel.PyannoteCommunity);
                    break;
                case "Diart":
                    SelectByTag(SpeechSeparationModelBox, SpeechSeparationModel.None.ToString());
                    DiarizationCheck.IsChecked = true;
                    SelectDiarizationModel(DiarizationModel.Diart);
                    break;
                case "Sortformer":
                    SelectByTag(SpeechSeparationModelBox, SpeechSeparationModel.None.ToString());
                    DiarizationCheck.IsChecked = true;
                    SelectDiarizationModel(DiarizationModel.Sortformer);
                    break;
                default:
                    SelectByTag(SpeechSeparationModelBox, SpeechSeparationModel.None.ToString());
                    DiarizationCheck.IsChecked = false;
                    break;
            }
        }
        finally
        {
            suppressSettingsChange = previousSuppression;
        }
    }

    private DiarizationModel AutomaticDiarizationModel()
    {
        var engine = ParseSelectedTag(AsrEngineBox, settings.AsrEngine);
        return engine == AsrEngine.WhisperLiveKitSortformer
            ? DiarizationModel.Sortformer
            : DiarizationModel.PyannoteCommunity;
    }

    private string AutomaticSpeakerProcessingModelName()
    {
        var effectiveSeparation = SpeechSeparationAdvisor.Resolve(
            SpeechSeparationModel.Auto,
            speechSeparationRecommendation);
        return effectiveSeparation != SpeechSeparationModel.None
            ? SpeechSeparationAdvisor.DisplayName(effectiveSeparation)
            : LocalizedDiarizationModelName(AutomaticDiarizationModel());
    }

    private void UpdateSpeakerProcessingUiState(SpeechSeparationModel? requested = null)
    {
        if (SpeakerProcessingOptionsPanel is null ||
            SpeakerProcessingStatusText is null ||
            SpeakerProcessingEffectiveText is null)
        {
            return;
        }

        _ = requested;
        ApplySpeakerProcessingChoiceToState();
        var choice = SelectedSpeakerProcessingChoice();
        var selectedSeparation = ParseSelectedTag(
            SpeechSeparationModelBox,
            settings.SpeechSeparationModel);
        var effectiveSeparation = SpeechSeparationAdvisor.Resolve(
            selectedSeparation,
            speechSeparationRecommendation);
        var separationActive = effectiveSeparation != SpeechSeparationModel.None;
        var identificationEnabled = DiarizationCheck.IsChecked == true;
        var activeDiarizationModel = SelectedDiarizationModel();
        var processingOff = !separationActive && !identificationEnabled;
        var identificationActive = !separationActive && identificationEnabled;
        var presetAvailable = identificationActive && activeDiarizationModel is not DiarizationModel.Sortformer;

        ApplySpeakerProcessingConstraints(separationActive, identificationActive);
        SpeakerProcessingOptionsPanel.IsEnabled = !processingOff;
        SpeakerProcessingOptionsPanel.Opacity = processingOff ? 0.45 : 1.0;
        DiarizationPresetPanel.IsEnabled = presetAvailable;
        DiarizationPresetPanel.Opacity = presetAvailable ? 1.0 : 0.45;
        SpeakerCountPanel.IsEnabled = identificationActive;
        SpeakerCountPanel.Opacity = identificationActive ? 1.0 : 0.55;
        SpeakerCountModePanel.IsEnabled = identificationActive;
        SpeakerCountModePanel.Opacity = identificationActive ? 1.0 : 0.55;

        if (separationActive)
        {
            SpeakerProcessingStatusText.Text = LF(
                "SpeakerProcessingStatusSeparation",
                SpeechSeparationAdvisor.DisplayName(effectiveSeparation));
        }
        else
        {
            SpeakerProcessingStatusText.Text = identificationEnabled
                ? LF("SpeakerProcessingStatusIdentification", LocalizedDiarizationModelName(activeDiarizationModel))
                : L("SpeakerProcessingStatusOff");
        }

        var activeModelName = separationActive
            ? SpeechSeparationAdvisor.DisplayName(effectiveSeparation)
            : identificationEnabled
                ? LocalizedDiarizationModelName(activeDiarizationModel)
                : L("SpeechSeparationOff");
        SpeakerProcessingEffectiveText.Text = choice == "Auto"
            ? LF("SpeakerProcessingEffectiveAuto", activeModelName)
            : LF("SpeakerProcessingEffectiveManual", activeModelName);
        UpdateModelDetailsPanel(separationActive, identificationActive, effectiveSeparation, activeDiarizationModel);
    }

    private void ApplySpeakerProcessingConstraints(bool separationActive, bool identificationActive)
    {
        var previousSuppression = suppressSettingsChange;
        suppressSettingsChange = true;
        try
        {
            MaxSpeakers1Radio.IsEnabled = false;
            if (separationActive)
            {
                MaxSpeakers2Radio.IsChecked = true;
                SpeakerModeExactRadio.IsChecked = true;
                return;
            }

            if (identificationActive && SelectedSpeakerCountMax() < 2)
            {
                MaxSpeakers2Radio.IsChecked = true;
            }
        }
        finally
        {
            suppressSettingsChange = previousSuppression;
        }
    }

    private void UpdateModelDetailsPanel(
        bool separationActive,
        bool identificationActive,
        SpeechSeparationModel effectiveSeparation,
        DiarizationModel activeDiarizationModel)
    {
        if (SelectedAsrModelTitleText is null || SelectedSpeakerModelTitleText is null)
        {
            return;
        }

        var engine = ParseSelectedTag(AsrEngineBox, settings.AsrEngine);
        var engineName = SelectedContent(AsrEngineBox, engine.ToString());
        var sttModel = SelectedContent(SttModelBox, settings.SttModel);
        SelectedAsrModelTitleText.Text = LF("SelectedAsrModelTitle", engineName, sttModel);
        SelectedAsrModelDescriptionText.Text = engine switch
        {
            AsrEngine.Qwen3Asr => LF("AsrModelDetailsQwen", sttModel),
            AsrEngine.WhisperLiveKitSortformer => LF("AsrModelDetailsWhisperLiveKit", sttModel),
            AsrEngine.WhisperX => LF("AsrModelDetailsWhisperX", sttModel),
            _ => LF("AsrModelDetailsFasterWhisper", sttModel)
        };

        var choice = SelectedSpeakerProcessingChoice();
        var speakerModelName = separationActive
            ? SpeechSeparationAdvisor.DisplayName(effectiveSeparation)
            : identificationActive
                ? LocalizedDiarizationModelName(activeDiarizationModel)
                : L("SpeechSeparationOff");
        var selectionMode = choice == "Auto" ? L("AutomaticSelection") : L("ManualSelection");
        SelectedSpeakerModelTitleText.Text = LF("SelectedSpeakerModelTitle", speakerModelName, selectionMode);
        SelectedSpeakerModelDescriptionText.Text = separationActive
            ? effectiveSeparation == SpeechSeparationModel.MossFormer2
                ? L("SpeakerModelMossDescription")
                : L("SpeakerModelSepFormerDescription")
            : identificationActive
                ? activeDiarizationModel switch
                {
                    DiarizationModel.Diart => L("SpeakerModelDiartDescription"),
                    DiarizationModel.Sortformer => L("SpeakerModelSortformerDescription"),
                    _ => L("SpeakerModelCommunityDescription")
                }
                : L("SpeakerModelOffDescription");

        if (separationActive)
        {
            CurrentSpeakerSettingsText.Text = L("SpeakerSettingsSeparated");
            var assessment = SpeechSeparationAdvisor.Assess(
                hardwareProfile,
                ParseSelectedTag(ComputeModeBox, settings.ComputeMode),
                engine,
                effectiveSeparation,
                sttModel);
            ModelCompatibilityText.Text = LocalizedSpeechSeparationAssessment(assessment);
            return;
        }

        if (!identificationActive)
        {
            CurrentSpeakerSettingsText.Text = L("SpeakerSettingsOff");
            ModelCompatibilityText.Text = L("SpeakerModelOffCompatibility");
            return;
        }

        var speakerCount = SelectedSpeakerCountMax();
        var speakerMode = SelectedSpeakerCountMode() == SpeakerCountMode.Exact
            ? LF("ExactSpeakers", speakerCount)
            : LF("AutoMax", speakerCount);
        var preset = activeDiarizationModel == DiarizationModel.Sortformer
            ? L("AutomaticSelection")
            : SttPresetName(SelectedDiarizationQualityPreset());
        CurrentSpeakerSettingsText.Text = LF("SpeakerSettingsIdentification", preset, speakerMode);
        ModelCompatibilityText.Text = activeDiarizationModel == DiarizationModel.Sortformer
            ? L("SpeakerModelSortformerCompatibility")
            : L("SpeakerModelHfCompatibility");
    }

    private string LocalizedDiarizationModelName(DiarizationModel model)
    {
        return model switch
        {
            DiarizationModel.Diart => L("DiartRealtime"),
            DiarizationModel.Sortformer => L("Sortformer"),
            _ => L("PyannoteCommunity")
        };
    }

    private SpeechSeparationModel SelectedEffectiveSpeechSeparationModel()
    {
        var requested = ParseSelectedTag(
            SpeechSeparationModelBox,
            settings.SpeechSeparationModel);
        return SpeechSeparationAdvisor.Resolve(requested, speechSeparationRecommendation);
    }

    private string LocalizedSpeechSeparationAssessment(SpeechSeparationAssessment assessment)
    {
        return assessment.BlockReason switch
        {
            SpeechSeparationBlockReason.CpuMode => L("SpeechSeparationReasonCpu"),
            SpeechSeparationBlockReason.StreamingAsr => L("SpeechSeparationReasonStreamingAsr"),
            SpeechSeparationBlockReason.NvidiaGpuRequired => L("SpeechSeparationReasonNoGpu"),
            SpeechSeparationBlockReason.InsufficientGpuMemory => LF(
                "SpeechSeparationInsufficientVram",
                hardwareProfile.GpuMemoryGiB,
                assessment.RequiredGpuMemoryBytes / (double)SpeechSeparationAdvisor.GiB),
            SpeechSeparationBlockReason.InsufficientSystemMemory => LF(
                "SpeechSeparationInsufficientRam",
                hardwareProfile.MemoryGiB,
                assessment.RequiredSystemMemoryBytes / (double)SpeechSeparationAdvisor.GiB),
            _ => LF(
                "SpeechSeparationRequirements",
                assessment.RequiredGpuMemoryBytes / (double)SpeechSeparationAdvisor.GiB,
                assessment.RequiredSystemMemoryBytes / (double)SpeechSeparationAdvisor.GiB)
        };
    }

    private static string CompactSpeechSeparationDisplayName(SpeechSeparationModel model)
    {
        return model switch
        {
            SpeechSeparationModel.MossFormer2 => "MossFormer2",
            SpeechSeparationModel.SepFormerWhamr16k => "SepFormer",
            _ => SpeechSeparationAdvisor.DisplayName(model)
        };
    }

    private string LocalizedSpeechSeparationReason(ComputeMode computeMode, AsrEngine asrEngine)
    {
        if (computeMode == ComputeMode.Cpu)
        {
            return L("SpeechSeparationReasonCpu");
        }

        if (asrEngine == AsrEngine.WhisperLiveKitSortformer)
        {
            return L("SpeechSeparationReasonStreamingAsr");
        }

        if (!hardwareProfile.HasNvidiaGpu)
        {
            return L("SpeechSeparationReasonNoGpu");
        }

        return speechSeparationRecommendation.Model == SpeechSeparationModel.MossFormer2
            ? L("SpeechSeparationReasonMoss")
            : speechSeparationRecommendation.Model == SpeechSeparationModel.SepFormerWhamr16k
                ? L("SpeechSeparationReasonSepFormer")
                : L("SpeechSeparationReasonMemory");
    }

    private SpeechSeparationModel EffectiveSpeechSeparationModel()
    {
        return SpeechSeparationAdvisor.Resolve(
            settings.SpeechSeparationModel,
            speechSeparationRecommendation);
    }

    private void ShowOverlay(bool rememberOpen)
    {
        if (overlayWindow != null && overlayWindow.IsVisible)
        {
            if (rememberOpen && !settings.OverlayOpen)
            {
                settings.OverlayOpen = true;
                settingsStore.Save(settings);
            }

            return;
        }

        overlayWindow = new OverlayWindow(localizer, settings.Overlay);
        overlayWindow.OverlaySettingsChanged += OverlayWindow_SettingsChanged;
        overlayWindow.Closed += OverlayWindow_Closed;
        overlayWindow.SeedEntries(LiveEntryViewModels(), settings.OverlayDisplayLines);
        overlayWindow.Show();
        if (rememberOpen && !settings.OverlayOpen)
        {
            settings.OverlayOpen = true;
            settingsStore.Save(settings);
        }
    }

    private void CloseOverlay(bool rememberClosed)
    {
        if (overlayWindow == null)
        {
            if (rememberClosed && settings.OverlayOpen)
            {
                settings.OverlayOpen = false;
                settingsStore.Save(settings);
            }

            return;
        }

        if (rememberClosed && settings.OverlayOpen)
        {
            settings.OverlayOpen = false;
            settingsStore.Save(settings);
        }

        overlayWindow.Close();
    }

    private void OverlayWindow_Closed(object? sender, EventArgs e)
    {
        if (!closingApp && settings.OverlayOpen)
        {
            settings.OverlayOpen = false;
            settingsStore.Save(settings);
        }

        if (ReferenceEquals(sender, overlayWindow))
        {
            overlayWindow = null;
        }
    }

    private void OverlayWindow_SettingsChanged(object? sender, OverlayWindowSettings e)
    {
        settings.Overlay = e;
        if (!suppressSettingsChange)
        {
            suppressSettingsChange = true;
            try
            {
                OverlayOpacitySlider.Value = Math.Clamp(settings.Overlay.Opacity * 100.0, 0.0, 100.0);
                OverlayClickThroughCheck.IsChecked = settings.Overlay.ClickThrough;
            }
            finally
            {
                suppressSettingsChange = false;
            }
        }

        settingsStore.Save(settings);
    }

    private void ResetOverlayButton_Click(object sender, RoutedEventArgs e)
    {
        settings.Overlay = OverlayWindowSettings.Default();
        OverlayOpacitySlider.Value = settings.Overlay.Opacity * 100.0;
        OverlayClickThroughCheck.IsChecked = settings.Overlay.ClickThrough;
        settingsStore.Save(settings);
        ApplyCaptionSpeakerColors();
        overlayWindow?.ApplyOverlaySettings(settings.Overlay);
        SetStatus(L("SettingsSaved"));
    }

    private void OverlayColorsButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new OverlayColorWindow(settings.Overlay, localizer)
        {
            Owner = this
        };

        if (window.ShowDialog() != true)
        {
            return;
        }

        settings.Overlay = window.Settings;
        OverlayOpacitySlider.Value = Math.Clamp(settings.Overlay.Opacity * 100.0, 0.0, 100.0);
        OverlayClickThroughCheck.IsChecked = settings.Overlay.ClickThrough;
        settingsStore.Save(settings);
        ApplyCaptionSpeakerColors();
        overlayWindow?.ApplyOverlaySettings(settings.Overlay);
        SetStatus(L("SettingsSaved"));
    }

    private void TopmostButton_Click(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        settings.Topmost = Topmost;
        settingsStore.Save(settings);
        TopmostButton.Opacity = Topmost ? 1 : 0.45;
    }

    private void ModelManagerButton_Click(object sender, RoutedEventArgs e)
    {
        OpenModelManager(showAccessNotice: false);
    }

    private void TranslateApiSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new TranslationApiWindow(settings.GoogleTranslateApiKey, localizer)
        {
            Owner = this
        };

        if (window.ShowDialog() != true)
        {
            return;
        }

        settings.GoogleTranslateApiKey = window.GoogleApiKey;
        settingsStore.Save(settings);
        UpdateTranslationProviderAvailabilityText();
        ClearTranslations();
        ApplyDisplaySettings();
        SetStatus(L("TranslationApiKeySaved"));
    }

    private async void DetailActionButton_Click(object sender, RoutedEventArgs e)
    {
        switch (detailActionKind)
        {
            case SetupActionKind.HuggingFaceToken:
                await OpenHuggingFaceAccessAndRetryAsync();
                break;
            case SetupActionKind.InstallWorker:
                await RepairWorkerAsync();
                break;
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    protected override async void OnClosed(EventArgs e)
    {
        closingApp = true;
        captionInactivityTimer.Stop();
        if (overlayWindow != null)
        {
            settings.Overlay = overlayWindow.CaptureOverlaySettings();
            settings.OverlayOpen = overlayWindow.IsVisible;
            settingsStore.Save(settings);
        }

        audioCapture.Dispose();
        foreach (var request in translationRequests.Values)
        {
            request.Cancel();
            request.Dispose();
        }

        translationRequests.Clear();
        settingsApplyCts?.Cancel();
        settingsApplyCts?.Dispose();
        translationService.Dispose();
        await workerClient.StopAsync();
        workerClient.Dispose();
        overlayWindow?.Close();
        base.OnClosed(e);
    }

    private void LoadSettingsIntoUi()
    {
        suppressSettingsChange = true;
        try
        {
            SelectByTag(InputModeBox, settings.InputMode.ToString());
            UpdateAudioInputUiState();
            SelectByTag(AsrEngineBox, settings.AsrEngine.ToString());
            SelectByContent(SttModelBox, settings.SttModel);
            UpdateSttLanguagesButton();
            SelectSttPreset(settings.SttQualityPreset);
            SelectDiarizationPreset(settings.DiarizationQualityPreset);
            OverlayOpacitySlider.Value = Math.Clamp(settings.Overlay.Opacity * 100.0, 0.0, 100.0);
            SelectByTag(ComputeModeBox, settings.ComputeMode.ToString());
            PopulateSpeechSeparationModelItems(
                settings.SpeechSeparationModel,
                settings.ComputeMode,
                settings.AsrEngine,
                settings.SttModel);
            TranslationEnabledCheck.IsChecked = settings.TranslationEnabled;
            SelectByTag(TranslateProviderBox, settings.TranslateProvider.ToString());
            SelectByTag(TargetLanguageBox, settings.TargetLanguage);
            SelectCaptionDisplayMode(settings.CaptionDisplayMode);
            UpdateTranslationUiState();
            SelectSpeakerCount(settings);
            SelectSpeakerCountMode(settings.SpeakerCountMode);
            SelectByContent(CaptionDisplayLinesBox, settings.CaptionDisplayLines.ToString());
            SelectByContent(OverlayDisplayLinesBox, settings.OverlayDisplayLines.ToString());
            OverlayClickThroughCheck.IsChecked = settings.Overlay.ClickThrough;
            DiarizationCheck.IsChecked = settings.DiarizationEnabled;
            NormalizeDiarizationForSpeakerCount();
            SelectDiarizationModel(settings.DiarizationModel);
            PopulateSpeakerProcessingModelItems(
                SpeakerProcessingChoiceFromState(),
                settings.ComputeMode,
                settings.AsrEngine,
                settings.SttModel);
            DiartManualCheck.IsChecked = settings.DiartManualSettings;
            SetDoubleText(DiartDurationBox, settings.DiartDurationSeconds);
            SetDoubleText(DiartStepBox, settings.DiartStepSeconds);
            SetDoubleText(DiartLatencyBox, settings.DiartLatencySeconds);
            SetDoubleText(DiartTauBox, settings.DiartTauActive);
            SetDoubleText(DiartRhoBox, settings.DiartRhoUpdate);
            SetDoubleText(DiartDeltaBox, settings.DiartDeltaNew);
            ApplyAsrEngineUiState(normalizeSelection: true);
            NormalizeAsrEngineSettings();
            UpdateSpeakerProcessingUiState();
            UpdateDiartManualControls();
            UpdateSttPresetSummary();
        }
        finally
        {
            suppressSettingsChange = false;
        }
    }

    private void SaveSettingsFromUi()
    {
        var previousPythonConsoleModelKey = pythonConsoleModelKey;
        var previousTranslationEnabled = settings.TranslationEnabled;
        var previousProvider = settings.TranslateProvider;
        var previousTargetLanguage = settings.TargetLanguage;
        settings.InputMode = ParseSelectedTag(InputModeBox, settings.InputMode);
        settings.AsrEngine = ParseSelectedTag(AsrEngineBox, settings.AsrEngine);
        ApplyAsrEngineUiState(normalizeSelection: true);
        settings.SttModel = SelectedContent(SttModelBox, settings.SttModel);
        settings.SttQualityPreset = SelectedSttQualityPreset();
        settings.DiarizationQualityPreset = SelectedDiarizationQualityPreset();
        settings.ComputeMode = ParseSelectedTag(ComputeModeBox, settings.ComputeMode);
        ApplySpeakerProcessingChoiceToState();
        settings.SpeechSeparationModel = ParseSelectedTag(
            SpeechSeparationModelBox,
            settings.SpeechSeparationModel);
        settings.TranslationEnabled = TranslationEnabledCheck.IsChecked == true;
        settings.TranslateProvider = ParseSelectedTag(TranslateProviderBox, settings.TranslateProvider);
        settings.TargetLanguage = ParseSelectedTag(TargetLanguageBox, settings.TargetLanguage);
        settings.CaptionDisplayMode = SelectedCaptionDisplayMode();
        SaveSpeakerCountFromUi();
        NormalizeDiarizationForSpeakerCount();
        settings.CaptionDisplayLines = int.TryParse(SelectedContent(CaptionDisplayLinesBox, settings.CaptionDisplayLines.ToString()), out var captionLines)
            ? captionLines
            : settings.CaptionDisplayLines;
        settings.OverlayDisplayLines = int.TryParse(SelectedContent(OverlayDisplayLinesBox, settings.OverlayDisplayLines.ToString()), out var overlayLines)
            ? overlayLines
            : settings.OverlayDisplayLines;
        settings.DisplayLines = Math.Max(settings.CaptionDisplayLines, settings.OverlayDisplayLines);
        settings.ShowLatency = true;
        settings.DiarizationEnabled = DiarizationCheck.IsChecked == true;
        settings.DiarizationModel = SelectedDiarizationModel();
        settings.DiartManualSettings = DiartManualCheck.IsChecked == true;
        settings.DiartDurationSeconds = ParseDoubleText(DiartDurationBox, settings.DiartDurationSeconds, 3.0, 12.0);
        settings.DiartStepSeconds = ParseDoubleText(DiartStepBox, settings.DiartStepSeconds, 0.25, 1.0);
        settings.DiartLatencySeconds = ParseDoubleText(DiartLatencyBox, settings.DiartLatencySeconds, 0.5, 5.0);
        settings.DiartTauActive = ParseDoubleText(DiartTauBox, settings.DiartTauActive, 0.3, 0.9);
        settings.DiartRhoUpdate = ParseDoubleText(DiartRhoBox, settings.DiartRhoUpdate, 0.0, 1.0);
        settings.DiartDeltaNew = ParseDoubleText(DiartDeltaBox, settings.DiartDeltaNew, 0.3, 2.0);
        NormalizeAsrEngineSettings();
        ClearPythonConsoleIfModelChanged(previousPythonConsoleModelKey);
        settings.Overlay.Opacity = Math.Clamp(OverlayOpacitySlider.Value / 100.0, 0.0, 1.0);
        settings.Overlay.ClickThrough = OverlayClickThroughCheck.IsChecked == true;
        if (previousTranslationEnabled != settings.TranslationEnabled ||
            previousProvider != settings.TranslateProvider ||
            !string.Equals(previousTargetLanguage, settings.TargetLanguage, StringComparison.OrdinalIgnoreCase))
        {
            ClearTranslations();
        }

        settingsStore.Save(settings);
    }

    private void ClearTranslations()
    {
        foreach (var request in translationRequests.Values)
        {
            request.Cancel();
        }

        translationRequests.Clear();
        translationTexts.Clear();
        translationSourceTexts.Clear();
        translationRateLimitNoticeUntil = DateTimeOffset.MinValue;
    }

    private void UpdateTranslationProviderAvailabilityText()
    {
        TranslationProviderAvailabilityText.Text = TranslationEnabledCheck.IsChecked != true
            ? L("TranslationDisabled")
            : string.IsNullOrWhiteSpace(settings.GoogleTranslateApiKey)
            ? L("TranslationProviderAvailability")
            : L("TranslationProviderConfigured");
    }

    private void UpdateTranslationUiState()
    {
        var enabled = TranslationEnabledCheck.IsChecked == true;
        TranslationConfigurationPanel.IsEnabled = enabled;
        TranslationConfigurationPanel.Opacity = enabled ? 1.0 : 0.5;
        DisplayTranslatedRadio.IsEnabled = enabled;
        DisplayBothRadio.IsEnabled = enabled;
        DisplayTranslatedRadio.ToolTip = enabled ? null : L("TranslationEnabledHelp");
        DisplayBothRadio.ToolTip = enabled ? null : L("TranslationEnabledHelp");
        UpdateTranslationProviderAvailabilityText();
    }

    private CaptionDisplayMode SelectedCaptionDisplayMode()
    {
        if (DisplayTranslatedRadio.IsChecked == true)
        {
            return CaptionDisplayMode.Translated;
        }

        if (DisplayBothRadio.IsChecked == true)
        {
            return CaptionDisplayMode.Both;
        }

        return CaptionDisplayMode.Original;
    }

    private void SelectCaptionDisplayMode(CaptionDisplayMode mode)
    {
        DisplayOriginalRadio.IsChecked = mode == CaptionDisplayMode.Original;
        DisplayTranslatedRadio.IsChecked = mode == CaptionDisplayMode.Translated;
        DisplayBothRadio.IsChecked = mode == CaptionDisplayMode.Both;
    }

    private int SelectedSttQualityPreset()
    {
        if (SttPresetTalkShowRadio.IsChecked == true)
        {
            return 100;
        }

        return SttPresetDebateRadio.IsChecked == true ? 50 : 0;
    }

    private void SelectSttPreset(int quality)
    {
        SttPresetSpeedRadio.IsChecked = quality < 35;
        SttPresetDebateRadio.IsChecked = quality >= 35 && quality < 75;
        SttPresetTalkShowRadio.IsChecked = quality >= 75;
    }

    private int SelectedDiarizationQualityPreset()
    {
        if (DiarizationPresetStableRadio.IsChecked == true)
        {
            return 100;
        }

        return DiarizationPresetBalancedRadio.IsChecked == true ? 50 : 0;
    }

    private void SelectDiarizationPreset(int quality)
    {
        DiarizationPresetSensitiveRadio.IsChecked = quality < 35;
        DiarizationPresetBalancedRadio.IsChecked = quality >= 35 && quality < 75;
        DiarizationPresetStableRadio.IsChecked = quality >= 75;
    }

    private DiarizationModel SelectedDiarizationModel()
    {
        if (DiarizationSortformerRadio.IsChecked == true)
        {
            return DiarizationModel.Sortformer;
        }

        return DiarizationDiartRadio.IsChecked == true ? DiarizationModel.Diart : DiarizationModel.PyannoteCommunity;
    }

    private void SelectDiarizationModel(DiarizationModel model)
    {
        DiarizationCommunityRadio.IsChecked = model == DiarizationModel.PyannoteCommunity;
        DiarizationDiartRadio.IsChecked = model == DiarizationModel.Diart;
        DiarizationSortformerRadio.IsChecked = model == DiarizationModel.Sortformer;
    }

    private void ApplyAsrEngineUiState(bool normalizeSelection)
    {
        var engine = ParseSelectedTag(AsrEngineBox, settings.AsrEngine);
        SyncSttModelItemsForEngine(engine, normalizeSelection);
        SttLanguagesButton.IsEnabled = true;
        DiarizationCheck.IsEnabled = true;
        DiarizationCheck.ClearValue(ToolTipProperty);
        DiarizationModelPanel.Visibility = Visibility.Visible;
        DiarizationCommunityRadio.IsEnabled = true;
        DiarizationDiartRadio.IsEnabled = true;
        DiarizationSortformerRadio.IsEnabled = true;
    }

    private void SyncSttModelItemsForEngine(AsrEngine engine, bool normalizeSelection)
    {
        var fasterWhisper = engine == AsrEngine.None;
        var qwenAsr = engine == AsrEngine.Qwen3Asr;
        var whisperLiveKit = engine == AsrEngine.WhisperLiveKitSortformer;
        var whisperX = engine == AsrEngine.WhisperX;

        SttDefaultItem.Visibility = whisperLiveKit ? Visibility.Visible : Visibility.Collapsed;
        SttTinyItem.Visibility = fasterWhisper || whisperX ? Visibility.Visible : Visibility.Collapsed;
        SttBaseItem.Visibility = fasterWhisper || whisperX ? Visibility.Visible : Visibility.Collapsed;
        SttSmallItem.Visibility = fasterWhisper || whisperX ? Visibility.Visible : Visibility.Collapsed;
        SttMediumItem.Visibility = fasterWhisper || whisperX ? Visibility.Visible : Visibility.Collapsed;
        SttLargeV3Item.Visibility = fasterWhisper || whisperX ? Visibility.Visible : Visibility.Collapsed;
        SttLargeV3TurboItem.Visibility = fasterWhisper || whisperX ? Visibility.Visible : Visibility.Collapsed;
        SttQwen06BItem.Visibility = qwenAsr ? Visibility.Visible : Visibility.Collapsed;
        SttQwen17BItem.Visibility = qwenAsr ? Visibility.Visible : Visibility.Collapsed;

        if (!normalizeSelection)
        {
            return;
        }

        var selectedModel = SelectedContent(SttModelBox, settings.SttModel);
        if (fasterWhisper && !IsFasterWhisperSttModel(selectedModel))
        {
            SelectByContent(SttModelBox, DefaultWhisperModel);
        }
        else if (whisperX && !IsFasterWhisperSttModel(selectedModel))
        {
            SelectByContent(SttModelBox, DefaultWhisperModel);
        }
        else if (qwenAsr && !IsQwenSttModel(selectedModel))
        {
            SelectByContent(SttModelBox, DefaultQwenModel);
        }
        else if (whisperLiveKit && !IsWhisperLiveKitSttModel(selectedModel))
        {
            SelectByContent(SttModelBox, DefaultWhisperLiveKitModel);
        }
    }

    private void NormalizeAsrEngineSettings()
    {
        if (settings.AsrEngine == AsrEngine.None && !IsFasterWhisperSttModel(settings.SttModel))
        {
            settings.SttModel = DefaultWhisperModel;
            SelectByContent(SttModelBox, settings.SttModel);
        }
        else if (settings.AsrEngine == AsrEngine.WhisperX && !IsFasterWhisperSttModel(settings.SttModel))
        {
            settings.SttModel = DefaultWhisperModel;
            SelectByContent(SttModelBox, settings.SttModel);
        }
        else if (settings.AsrEngine == AsrEngine.Qwen3Asr && !IsQwenSttModel(settings.SttModel))
        {
            settings.SttModel = DefaultQwenModel;
            SelectByContent(SttModelBox, settings.SttModel);
        }
        else if (settings.AsrEngine == AsrEngine.WhisperLiveKitSortformer && !IsWhisperLiveKitSttModel(settings.SttModel))
        {
            settings.SttModel = DefaultWhisperLiveKitModel;
            SelectByContent(SttModelBox, settings.SttModel);
        }
    }

    private void UpdateSttLanguagesButton()
    {
        SttLanguagesButton.Content = SttLanguageWindow.Summary(settings.SttLanguages, localizer);
        SttLanguagesButton.ToolTip = SttLanguagesButton.Content;
    }

    private void UpdateSttPresetSummary()
    {
        if (SttPresetSummaryText is null || SttPresetSpeedRadio is null || SttPresetDebateRadio is null || SttPresetTalkShowRadio is null)
        {
            return;
        }

        var sttQuality = SelectedSttQualityPreset();
        var diarizationQuality = SelectedDiarizationQualityPreset();
        var sttPresetName = SttPresetName(sttQuality);
        var diarizationPresetName = SttPresetName(diarizationQuality);
        var sttChunkSeconds = SttChunkSecondsForPreset(sttQuality);
        var beamSize = SttBeamSizeForPreset(sttQuality);
        var wordTimestamps = sttQuality >= 75 ? L("SttPresetWordOn") : L("SttPresetWordOff");
        var selectedEngine = ParseSelectedTag(AsrEngineBox, settings.AsrEngine);
        var effectiveSeparation = SelectedEffectiveSpeechSeparationModel();
        if (effectiveSeparation != SpeechSeparationModel.None)
        {
            SttPresetSummaryText.Text = LF(
                "SttPresetSummarySeparated",
                sttPresetName,
                sttChunkSeconds,
                beamSize,
                SpeechSeparationAdvisor.DisplayName(effectiveSeparation));
            return;
        }
        if (selectedEngine == AsrEngine.WhisperLiveKitSortformer)
        {
            SttPresetSummaryText.Text = LF("SttPresetSummaryWhisperLiveKit", sttPresetName, sttChunkSeconds, beamSize);
            return;
        }
        if (selectedEngine == AsrEngine.WhisperX)
        {
            SttPresetSummaryText.Text = LF("SttPresetSummaryWhisperX", sttPresetName, sttChunkSeconds, beamSize, wordTimestamps);
            return;
        }

        var diarizationModel = SelectedDiarizationModel();
        if (diarizationModel == DiarizationModel.Diart && DiartManualCheck.IsChecked == true)
        {
            var duration = ParseDoubleText(DiartDurationBox, settings.DiartDurationSeconds, 3.0, 12.0);
            var step = ParseDoubleText(DiartStepBox, settings.DiartStepSeconds, 0.25, 1.0);
            var latency = ParseDoubleText(DiartLatencyBox, settings.DiartLatencySeconds, 0.5, 5.0);
            SttPresetSummaryText.Text = LF("SttPresetSummaryDiartManual", diarizationPresetName, sttChunkSeconds, beamSize, wordTimestamps, duration, step, latency);
            return;
        }

        if (diarizationModel == DiarizationModel.Diart)
        {
            SttPresetSummaryText.Text = LF("SttPresetSummaryDiart", diarizationPresetName, sttChunkSeconds, beamSize, wordTimestamps, DiartDurationSecondsForPreset(diarizationQuality), DiartStepSecondsForPreset(diarizationQuality), DiartLatencySecondsForPreset(diarizationQuality));
            return;
        }

        if (diarizationModel == DiarizationModel.Sortformer)
        {
            SttPresetSummaryText.Text = LF("SttPresetSummarySortformer", diarizationPresetName, sttChunkSeconds, beamSize);
            return;
        }

        SttPresetSummaryText.Text = diarizationQuality >= 75
            ? LF("SttPresetSummaryCommunityStable", diarizationPresetName, sttChunkSeconds, beamSize, DiarizationContextSecondsForPreset(diarizationQuality))
            : LF("SttPresetSummaryCommunity", diarizationPresetName, sttChunkSeconds, beamSize, wordTimestamps, DiarizationContextSecondsForPreset(diarizationQuality));
    }

    private void UpdateDiartManualControls()
    {
        if (DiartManualPanel is null || DiartManualCheck is null || DiarizationCommunityRadio is null || DiarizationDiartRadio is null)
        {
            return;
        }

        var speakerIdentificationAvailable = SelectedEffectiveSpeechSeparationModel() == SpeechSeparationModel.None &&
            DiarizationCheck.IsChecked == true;
        var isDiart = speakerIdentificationAvailable &&
            SelectedDiarizationModel() == DiarizationModel.Diart;
        var enabled = isDiart && DiartManualCheck.IsChecked == true;
        DiartManualCheck.IsEnabled = isDiart;
        DiartManualCheck.Visibility = isDiart ? Visibility.Visible : Visibility.Collapsed;
        DiartManualDescriptionText.Visibility = isDiart ? Visibility.Visible : Visibility.Collapsed;
        DiartManualPanel.IsEnabled = enabled;
        DiartManualPanel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
    }

    private static int SttChunkSecondsForPreset(int quality)
    {
        if (quality >= 75)
        {
            return 5;
        }

        return quality >= 35 ? 4 : 2;
    }

    private static int SttBeamSizeForPreset(int quality)
    {
        if (quality >= 75)
        {
            return 5;
        }

        return quality >= 35 ? 3 : 1;
    }

    private string SttPresetName(int quality)
    {
        if (quality >= 75)
        {
            return L("Stable");
        }

        return quality >= 35 ? L("Balanced") : L("Sensitive");
    }

    private static int DiarizationContextSecondsForPreset(int quality)
    {
        if (quality >= 75)
        {
            return 120;
        }

        return quality >= 35 ? 60 : 30;
    }

    private static double DiartDurationSecondsForPreset(int quality)
    {
        _ = quality;
        return 5.0;
    }

    private static double DiartStepSecondsForPreset(int quality)
    {
        _ = quality;
        return 0.5;
    }

    private static double DiartLatencySecondsForPreset(int quality)
    {
        if (quality >= 75)
        {
            return 5.0;
        }

        return quality >= 35 ? 2.0 : 0.5;
    }

    private static bool IsQwenSttModel(string model)
    {
        return model.StartsWith("qwen3-asr-", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFasterWhisperSttModel(string model)
    {
        return model.Equals("tiny", StringComparison.OrdinalIgnoreCase)
            || model.Equals("base", StringComparison.OrdinalIgnoreCase)
            || model.Equals("small", StringComparison.OrdinalIgnoreCase)
            || model.Equals("medium", StringComparison.OrdinalIgnoreCase)
            || model.Equals("large-v3", StringComparison.OrdinalIgnoreCase)
            || model.Equals("large-v3-turbo", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWhisperLiveKitSttModel(string model)
    {
        return string.IsNullOrWhiteSpace(model)
            || model.Equals(DefaultWhisperLiveKitModel, StringComparison.OrdinalIgnoreCase)
            || model.Equals("Default", StringComparison.OrdinalIgnoreCase);
    }

    private async Task QueueSettingsApplyAsync(bool restartIfRunning)
    {
        if (suppressSettingsChange)
        {
            return;
        }

        settingsApplyNeedsRestart |= restartIfRunning;
        settingsApplyCts?.Cancel();
        settingsApplyCts?.Dispose();

        var cts = new CancellationTokenSource();
        settingsApplyCts = cts;
        try
        {
            await Task.Delay(350, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!ReferenceEquals(settingsApplyCts, cts) || cts.IsCancellationRequested)
        {
            return;
        }

        var shouldRestart = settingsApplyNeedsRestart;
        settingsApplyNeedsRestart = false;
        try
        {
            await ApplySettingsChangeAsync(shouldRestart);
        }
        finally
        {
            if (ReferenceEquals(settingsApplyCts, cts))
            {
                settingsApplyCts = null;
            }

            cts.Dispose();
        }
    }

    private async Task ApplySettingsChangeAsync(bool restartIfRunning)
    {
        if (applyingSettingsChange)
        {
            settingsApplyNeedsRestart |= restartIfRunning;
            return;
        }

        applyingSettingsChange = true;
        try
        {
            var wasRunning = audioCapture.IsRunning || workerClient.IsRunning;
            SaveSettingsFromUi();
            ApplyDisplaySettings();

            if (restartIfRunning && wasRunning)
            {
                SetStatus(L("Restarting"));
                await StopCaptureAsync(showStopped: false);
                await StartCaptureAsync(showCaptionsPage: false);
            }
            else if (!wasRunning)
            {
                SetStatus(L("SettingsSaved"));
            }
        }
        finally
        {
            applyingSettingsChange = false;
        }
    }

    private void ApplyDisplaySettings()
    {
        speakerNames = BuildSpeakerNameMap();

        var existingEntries = merger.Entries.ToList();
        merger = new CaptionMerger(MaxRetainedDisplayLines(), speakerNames);
        foreach (var entry in existingEntries)
        {
            merger.Apply(entry.IsFinal
                ? new FinalCaptionEvent(entry.SpeakerId, entry.Text, entry.StartMs, entry.EndMs, entry.LatencyMs)
                : new PartialCaptionEvent(entry.SpeakerId, entry.Text, entry.StartMs, entry.EndMs, entry.LatencyMs));
        }

        RenderFeed();

        foreach (var entry in merger.Entries)
        {
            StartTranslationForEntryAsync(entry);
        }

        var latest = merger.Entries.LastOrDefault();
        if (latest != null)
        {
            UpdateCurrentCaption(latest);
        }
        else if (activePage == AppPage.Captions)
        {
            ShowDefaultCaptionDetail();
        }

        overlayWindow?.ApplyOverlaySettings(settings.Overlay);
        overlayWindow?.UpdateEntries(LiveEntryViewModels(), settings.OverlayDisplayLines);
        UpdateDebugStateText();
    }

    private int MaxRetainedDisplayLines()
    {
        return Math.Max(1, Math.Max(settings.CaptionDisplayLines, settings.OverlayDisplayLines));
    }

    private void NormalizeDiarizationForSpeakerCount()
    {
        var choice = SelectedSpeakerProcessingChoice();
        if (SelectedSpeakerCountMax() == 1 && choice is not "Off")
        {
            MaxSpeakers2Radio.IsChecked = true;
        }
    }

    private void SaveSpeakerCountFromUi()
    {
        var tag = SelectedSpeakerCountTag();
        if (TryParseSpeakerCountTag(tag, out _, out var maxSpeakers))
        {
            settings.MaxSpeakers = maxSpeakers;
        }
        else if (int.TryParse(tag, out maxSpeakers))
        {
            settings.MaxSpeakers = maxSpeakers;
        }

        settings.SpeakerCountMode = SelectedSpeakerCountMode();
        settings.ExactSpeakers = settings.SpeakerCountMode == SpeakerCountMode.Exact ? settings.MaxSpeakers : null;
    }

    private static string SpeakerCountTag(AppSettings settings)
    {
        return $"auto:{settings.MaxSpeakers}";
    }

    private void SelectSpeakerCount(AppSettings settings)
    {
        var tag = SpeakerCountTag(settings);
        foreach (var radio in SpeakerCountPanel.Children.OfType<RadioButton>())
        {
            radio.IsChecked = string.Equals(radio.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase);
        }

        if (!SpeakerCountPanel.Children.OfType<RadioButton>().Any(radio => radio.IsChecked == true))
        {
            MaxSpeakers4Radio.IsChecked = true;
        }
    }

    private string SelectedSpeakerCountTag()
    {
        return SpeakerCountPanel.Children
            .OfType<RadioButton>()
            .FirstOrDefault(radio => radio.IsChecked == true)
            ?.Tag?.ToString() ?? "auto:4";
    }

    private SpeakerCountMode SelectedSpeakerCountMode()
    {
        return SpeakerModeExactRadio.IsChecked == true ? SpeakerCountMode.Exact : SpeakerCountMode.ActiveMax;
    }

    private void SelectSpeakerCountMode(SpeakerCountMode mode)
    {
        SpeakerModeExactRadio.IsChecked = mode == SpeakerCountMode.Exact;
        SpeakerModeActiveMaxRadio.IsChecked = mode != SpeakerCountMode.Exact;
    }

    private int SelectedSpeakerCountMax()
    {
        return TryParseSpeakerCountTag(SelectedSpeakerCountTag(), out _, out var maxSpeakers)
            ? maxSpeakers
            : settings.MaxSpeakers;
    }

    private static bool TryParseSpeakerCountTag(string tag, out int? exactSpeakers, out int maxSpeakers)
    {
        exactSpeakers = null;
        maxSpeakers = 0;

        var parts = tag.Split(':', 2);
        if (parts.Length != 2 || !int.TryParse(parts[1], out var count) || count <= 0)
        {
            return false;
        }

        if (parts[0].Equals("exact", StringComparison.OrdinalIgnoreCase))
        {
            exactSpeakers = null;
            maxSpeakers = count;
            return true;
        }

        if (parts[0].Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            maxSpeakers = count;
            return true;
        }

        return false;
    }

    private void SetStatus(string text)
    {
        StatusText.Text = LF("LogLine", text);
    }

    private string L(string key)
    {
        return localizer.Text(key);
    }

    private string LF(string key, params object[] args)
    {
        return localizer.Format(key, args);
    }

    private void ShowSetupProgress(string title, string detail, double? percent)
    {
        ShowCaptionDetailMode();
        SetCaptionPlaceholder(title, detail);
        SetDetailAction(SetupActionHint.None);
        InlineProgressBar.Visibility = Visibility.Visible;
        if (percent.HasValue)
        {
            InlineProgressBar.IsIndeterminate = false;
            InlineProgressBar.Value = Math.Clamp(percent.Value, 0, 1) * 100;
            DetailStatusText.Text = $"{InlineProgressBar.Value:0}%";
        }
        else
        {
            InlineProgressBar.IsIndeterminate = true;
            DetailStatusText.Text = L("Working");
        }
    }

    private void HideSetupProgressIfReady()
    {
        InlineProgressBar.IsIndeterminate = false;
        InlineProgressBar.Value = 0;
        InlineProgressBar.Visibility = Visibility.Collapsed;
    }

    private void ShowDetail(string title, string detail, string? status, SetupActionHint? actionHint = null)
    {
        ShowCaptionDetailMode();
        SetCaptionPlaceholder(title, detail);
        DetailStatusText.Text = status ?? "";
        SetDetailAction(actionHint ?? SetupActionHint.None);
    }

    private void ShowCaptionDetail(string title, string detail, string? status, SetupActionHint? actionHint = null)
    {
        if (activePage == AppPage.Captions)
        {
            ShowDetail(title, detail, status, actionHint);
        }
    }

    private void ShowDefaultCaptionDetail()
    {
        ShowDetail(
            settings.CaptionDisplayMode == CaptionDisplayMode.Original ? L("Original") : L("Translation"),
            L("WaitingForSpeech"),
            L("Ready"));
    }

    private void ShowPage(AppPage page)
    {
        activePage = page;
        CaptionPanel.Visibility = page == AppPage.Captions ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = page == AppPage.Settings ? Visibility.Visible : Visibility.Collapsed;
        InfoPanel.Visibility = page == AppPage.Info ? Visibility.Visible : Visibility.Collapsed;
        ConsolePanel.Visibility = page == AppPage.Console ? Visibility.Visible : Visibility.Collapsed;

        switch (page)
        {
            case AppPage.Captions:
                RenderFeed();
                ShowCaptionDetailMode();
                break;
            case AppPage.Settings:
                SetStatus(L("Settings"));
                AdjustWindowHeightToSettingsContent();
                break;
            case AppPage.Info:
                SetStatus(L("Info"));
                break;
            case AppPage.Console:
                SetStatus(L("Console"));
                break;
        }
    }

    private void AdjustWindowHeightToSettingsContent()
    {
        if (adjustingPageHeight)
        {
            return;
        }

        adjustingPageHeight = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                if (activePage != AppPage.Settings)
                {
                    return;
                }

                var availableWidth = Math.Max(MinWidth, ActualWidth > 0 ? ActualWidth : Width);
                SettingsPanel.Measure(new Size(Math.Max(1, availableWidth - 49), double.PositiveInfinity));
                var targetHeight = Math.Ceiling(28 + SettingsPanel.DesiredSize.Height + 8);
                targetHeight = Math.Clamp(targetHeight, MinHeight, SystemParameters.WorkArea.Height);
                var currentHeight = ActualHeight > 0 ? ActualHeight : Height;
                if (Math.Abs(targetHeight - currentHeight) < 1)
                {
                    return;
                }

                Height = targetHeight;
                var overflow = Top + targetHeight - SystemParameters.WorkArea.Bottom;
                if (overflow > 0)
                {
                    Top = Math.Max(SystemParameters.WorkArea.Top, Top - overflow);
                }
            }
            finally
            {
                adjustingPageHeight = false;
            }
        }));
    }

    private void ShowCaptionDetailMode()
    {
        SetCaptionPlaceholderVisible(captionSpeakers.Count == 0);
    }

    private void SetCaptureButtonRunning(bool running)
    {
        StartButton.ToolTip = running ? L("StopCapture") : L("StartCapture");
        StartPlayIcon.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        StartStopIcon.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetDetailAction(SetupActionHint actionHint)
    {
        detailActionKind = actionHint.Kind;
        DetailActionButton.Content = LocalizeActionLabel(actionHint);
        DetailActionButton.Visibility = actionHint.Kind == SetupActionKind.None ? Visibility.Collapsed : Visibility.Visible;
        DetailActionButton.IsEnabled = actionHint.Kind != SetupActionKind.None;
    }

    private string LocalizeActionLabel(SetupActionHint actionHint)
    {
        return actionHint.Kind switch
        {
            SetupActionKind.InstallWorker => L(actionHint.Label.Equals("Retry", StringComparison.OrdinalIgnoreCase) ? "Retry" : "Install"),
            SetupActionKind.HuggingFaceToken => L("SetAccess"),
            _ => actionHint.Label
        };
    }

    private async Task RepairWorkerAsync()
    {
        var restartCapture = audioCapture.IsRunning || workerClient.IsRunning;
        StartButton.IsEnabled = false;
        DetailActionButton.IsEnabled = false;

        try
        {
            SaveSettingsFromUi();
            await EnsureHardwareRecommendationAsync();
            SaveSettingsFromUi();
            var effectiveSpeechSeparationModel = EffectiveSpeechSeparationModel();
            lastSttUnavailableMessage = null;
            await StopCaptureAsync(showStopped: false);
            CurrentSpeakerText.Text = L("Original");
            SetCurrentCaptionText(L("InstallWorkerTitle"), "");
            ShowSetupProgress(L("InstallWorkerTitle"), L("InstallWorkerCaption"), null);
            await workerEnvironment.RepairAsync(settings, effectiveSpeechSeparationModel);
            HideSetupProgressIfReady();

            if (restartCapture)
            {
                ShowDetail(L("SetupReady"), L("SetupReadyRestart"), L("Ready"));
                await StartCaptureAsync();
            }
            else
            {
                ShowDetail(L("SetupReady"), L("SetupReadyStart"), L("Ready"));
            }
        }
        catch (Exception ex)
        {
            HideSetupProgressIfReady();
            var actionHint = SetupActionHints.ForSetupFailure(ex.Message);
            if (actionHint.Kind == SetupActionKind.None)
            {
                actionHint = new SetupActionHint(SetupActionKind.InstallWorker, "Retry");
            }

            ShowDetail(L("SetupFailed"), ex.Message, L("Error"), actionHint);
            CurrentSpeakerText.Text = L("Original");
            SetCurrentCaptionText(L("SetupFailedCaption"), "");
            SetStatus(L("SetupFailed"));
        }
        finally
        {
            StartButton.IsEnabled = true;
            if (detailActionKind != SetupActionKind.None)
            {
                DetailActionButton.IsEnabled = true;
            }
        }
    }

    private async Task OpenHuggingFaceAccessAndRetryAsync()
    {
        var saved = OpenModelManager(showAccessNotice: true);
        if (saved && settings.DiarizationEnabled && !string.IsNullOrWhiteSpace(settings.HuggingFaceToken))
        {
            ShowSetupProgress(L("RetryingDiarizationSetup"), L("HfSavedRetrying"), null);
            if (audioCapture.IsRunning || workerClient.IsRunning)
            {
                await StopCaptureAsync(showStopped: false);
            }

            await StartCaptureAsync();
            return;
        }

        if (settings.DiarizationEnabled)
        {
            ShowDetail(
                L("LocalDiarizationSetup"),
                L("HfAccessInstructions"),
                L("ActionNeeded"),
                new SetupActionHint(SetupActionKind.HuggingFaceToken, "Set Access"));
            SetStatus(L("HfAccessNeeded"));
        }
    }

    private bool OpenModelManager(bool showAccessNotice)
    {
        SaveSettingsFromUi();
        var window = new ModelManagerWindow(
            paths,
            settings,
            localizer,
            EffectiveSpeechSeparationModel(),
            showAccessNotice)
        {
            Owner = this
        };
        if (window.ShowDialog() == true)
        {
            settingsStore.Save(settings);
            ShowDetail(L("HfAccessSaved"), L("HfAccessSavedDetail"), L("Ready"));
            return true;
        }

        return false;
    }

    private void ProjectLink_Click(object sender, RoutedEventArgs e)
    {
        OpenExternalLink(ProjectLinks.ProjectUrl);
    }

    private void ReferenceLink_Click(object sender, RoutedEventArgs e)
    {
        OpenExternalLink(ProjectLinks.ReferenceProjectUrl);
    }

    private void FasterWhisperLink_Click(object sender, RoutedEventArgs e)
    {
        OpenExternalLink(ProjectLinks.FasterWhisperUrl);
    }

    private void QwenAsrLink_Click(object sender, RoutedEventArgs e)
    {
        OpenExternalLink(ProjectLinks.QwenAsrUrl);
    }

    private void WhisperLiveKitLink_Click(object sender, RoutedEventArgs e)
    {
        OpenExternalLink(ProjectLinks.WhisperLiveKitUrl);
    }

    private void WhisperXLink_Click(object sender, RoutedEventArgs e)
    {
        OpenExternalLink(ProjectLinks.WhisperXUrl);
    }

    private void PyannoteLink_Click(object sender, RoutedEventArgs e)
    {
        OpenExternalLink(ProjectLinks.PyannoteAudioUrl);
    }

    private void DiartLink_Click(object sender, RoutedEventArgs e)
    {
        OpenExternalLink(ProjectLinks.DiartUrl);
    }

    private void SortformerLink_Click(object sender, RoutedEventArgs e)
    {
        OpenExternalLink(ProjectLinks.SortformerUrl);
    }

    private void MossFormer2Link_Click(object sender, RoutedEventArgs e)
    {
        OpenExternalLink(ProjectLinks.MossFormer2Url);
    }

    private void SepFormerLink_Click(object sender, RoutedEventArgs e)
    {
        OpenExternalLink(ProjectLinks.SepFormerUrl);
    }

    private void LicenseLink_Click(object sender, RoutedEventArgs e)
    {
        OpenExternalLink(ProjectLinks.LicenseUrl);
    }

    private static void OpenExternalLink(string url)
    {
        ExternalLinkService.OpenUrl(url);
    }

    private void OpenModelsFolder_Click(object sender, RoutedEventArgs e)
    {
        OpenFolder(paths.ModelDirectory);
    }

    private void OpenRuntimeFolder_Click(object sender, RoutedEventArgs e)
    {
        OpenFolder(paths.RuntimeDirectory);
    }

    private SpeakerNameMap BuildSpeakerNameMap()
    {
        return new SpeakerNameMap(localizer.Language);
    }

    private static string AppVersionText()
    {
        var assembly = Assembly.GetExecutingAssembly();
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
               ?? assembly.GetName().Version?.ToString()
               ?? "dev";
    }

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }

    private static void SelectByTag(ComboBox comboBox, string tag)
    {
        foreach (ComboBoxItem item in comboBox.Items)
        {
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }

        comboBox.SelectedIndex = 0;
    }

    private static void SelectByContent(ComboBox comboBox, string content)
    {
        foreach (ComboBoxItem item in comboBox.Items)
        {
            if (string.Equals(item.Content?.ToString(), content, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }

        comboBox.SelectedIndex = 0;
    }

    private static T ParseSelectedTag<T>(ComboBox comboBox, T fallback)
        where T : struct
    {
        return Enum.TryParse<T>(SelectedTag(comboBox), ignoreCase: true, out var value) ? value : fallback;
    }

    private static string ParseSelectedTag(ComboBox comboBox, string fallback)
    {
        var tag = SelectedTag(comboBox);
        return string.IsNullOrWhiteSpace(tag) ? fallback : tag;
    }

    private static double ParseDoubleText(TextBox textBox, double fallback, double min, double max)
    {
        return double.TryParse(textBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, min, max)
            : Math.Clamp(fallback, min, max);
    }

    private static void SetDoubleText(TextBox textBox, double value)
    {
        textBox.Text = value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static string SelectedTag(ComboBox comboBox)
    {
        return comboBox.SelectedItem is ComboBoxItem item ? item.Tag?.ToString() ?? "" : "";
    }

    private static string SelectedContent(ComboBox comboBox, string fallback)
    {
        return comboBox.SelectedItem is ComboBoxItem item ? item.Content?.ToString() ?? fallback : fallback;
    }

    private enum AppPage
    {
        Captions,
        Settings,
        Console,
        Info
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int dwAttribute,
        ref int pvAttribute,
        int cbAttribute);
}
