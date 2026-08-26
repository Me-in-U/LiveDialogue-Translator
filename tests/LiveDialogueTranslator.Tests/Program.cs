using LiveDialogueTranslator.Core.History;
using LiveDialogueTranslator.Core.Localization;
using LiveDialogueTranslator.Core.Protocol;
using LiveDialogueTranslator.Core.Runtime;
using LiveDialogueTranslator.Core.Speakers;
using LiveDialogueTranslator.Core.Startup;
using LiveDialogueTranslator.Core.Transcripts;

var tests = new (string Name, Action Body)[]
{
    ("worker protocol serializes configure commands as json lines", WorkerProtocolSerializesConfigureCommand),
    ("worker protocol serializes exact speaker count", WorkerProtocolSerializesExactSpeakerCount),
    ("worker protocol serializes speaker count mode", WorkerProtocolSerializesSpeakerCountMode),
    ("worker protocol serializes diarization model", WorkerProtocolSerializesDiarizationModel),
    ("worker protocol serializes ASR engine", WorkerProtocolSerializesAsrEngine),
    ("worker protocol serializes speech separation model", WorkerProtocolSerializesSpeechSeparationModel),
    ("worker protocol serializes manual diart tuning", WorkerProtocolSerializesManualDiartTuning),
    ("worker protocol parses final captions with speaker and latency", WorkerProtocolParsesFinalCaption),
    ("speaker names prefer manual rename over generated label", SpeakerNamesPreferManualRename),
    ("speaker names display unknown speaker", SpeakerNamesDisplayUnknownSpeaker),
    ("caption merger overwrites same speaker partial before final", CaptionMergerOverwritesPartial),
    ("caption merger preserves partial id while same speaker text updates", CaptionMergerPreservesPartialIdWhileSameSpeakerTextUpdates),
    ("caption merger appends adjacent final text from same speaker", CaptionMergerAppendsAdjacentFinalTextFromSameSpeaker),
    ("caption merger appends same speaker after interleaved event", CaptionMergerAppendsSameSpeakerAfterInterleavedEvent),
    ("caption merger keeps display line count per speaker", CaptionMergerKeepsDisplayLineCountPerSpeaker),
    ("speaker segment timeline chooses dominant overlap", SpeakerSegmentTimelineChoosesDominantOverlap),
    ("speaker segment timeline falls back to latest nearby speaker", SpeakerSegmentTimelineFallsBackToLatestNearbySpeaker),
    ("history store round trips final caption entries", HistoryStoreRoundTripsFinalCaptionEntries),
    ("history store upserts merged caption entries", HistoryStoreUpsertsMergedCaptionEntries),
    ("history store can clear entries", HistoryStoreCanClearEntries),
    ("hugging face token link points to access token settings", HuggingFaceTokenLinkPointsToAccessTokenSettings),
    ("project links expose reference and model pages", ProjectLinksExposeReferenceAndModelPages),
    ("project links expose all speech backend pages", ProjectLinksExposeAllSpeechBackendPages),
    ("installer shows apache license", InstallerShowsApacheLicense),
    ("installer uses korean wizard language", InstallerUsesKoreanWizardLanguage),
    ("release version is 1.1.0", ReleaseVersionIs110),
    ("main window exposes project and license info links", MainWindowExposesProjectAndLicenseInfoLinks),
    ("main window info page lists all speech backends", MainWindowInfoPageListsAllSpeechBackends),
    ("hugging face token guidance names required permission", HuggingFaceTokenGuidanceNamesRequiredPermission),
    ("language resolver follows windows ui culture", LanguageResolverFollowsWindowsUiCulture),
    ("python runtime layout uses downloaded app managed python", PythonRuntimeLayoutUsesDownloadedAppManagedPython),
    ("pip commands suppress script location warnings", PythonPipCommandsSuppressScriptLocationWarnings),
    ("pip commands install cuda torch from pytorch cu128 index", PythonPipCommandsInstallCudaTorchFromCu128Index),
    ("pip commands install diart without dependency resolver conflict", PythonPipCommandsInstallDiartWithoutDependencyResolverConflict),
    ("worker requirements pin pyannote before torchcodec dependency line", WorkerRequirementsPinPyannoteBeforeTorchcodecDependencyLine),
    ("python process environment uses utf8 and plain pip output", PythonProcessEnvironmentUsesUtf8AndPlainPipOutput),
    ("worker stderr classifier ignores benign gpu library warnings", WorkerStderrClassifierIgnoresBenignGpuLibraryWarnings),
    ("worker client filters benign stderr warnings", WorkerClientFiltersBenignStderrWarnings),
    ("app publish includes worker support modules", AppPublishIncludesWorkerSupportModules),
    ("caption page uses overlay-style speaker card", CaptionPageUsesOverlayStyleSpeakerCard),
    ("caption page removes inactive speakers", CaptionPageRemovesInactiveSpeakers),
    ("caption speaker label columns stay compact", CaptionSpeakerLabelColumnsStayCompact),
    ("worker client exports hugging face token to capture worker", WorkerClientExportsHuggingFaceTokenToCaptureWorker),
    ("main window passes saved hugging face token when starting capture", MainWindowPassesSavedHuggingFaceTokenWhenStartingCapture),
    ("main window preserves saved stt model when combo selection is empty", MainWindowPreservesSavedSttModelWhenComboSelectionIsEmpty),
    ("worker client restarts existing worker before applying new configuration", WorkerClientRestartsExistingWorkerBeforeApplyingNewConfiguration),
    ("worker client writes stdin without utf8 bom", WorkerClientWritesStdinWithoutUtf8Bom),
    ("worker client exposes raw python logs", WorkerClientExposesRawPythonLogs),
    ("worker environment exposes setup python logs", WorkerEnvironmentExposesSetupPythonLogs),
    ("main window preserves console scroll while reviewing logs", MainWindowPreservesConsoleScrollWhileReviewingLogs),
    ("main window wraps console logs and exposes console controls", MainWindowWrapsConsoleLogsAndExposesConsoleControls),
    ("main window clears python console when model selection changes", MainWindowClearsPythonConsoleWhenModelSelectionChanges),
    ("worker client waits for listening before returning start", WorkerClientWaitsForListeningBeforeReturningStart),
    ("main window removes default white resize border", MainWindowRemovesDefaultWhiteResizeBorder),
    ("main window uses rounded outer corners", MainWindowUsesRoundedOuterCorners),
    ("main window has console page and inline debug state", MainWindowHasConsolePageAndInlineDebugState),
    ("main window removes history page", MainWindowRemovesHistoryPage),
    ("translation service uses google provider and dummy providers", TranslationServiceUsesGoogleProviderAndDummyProviders),
    ("main window exposes translation provider and display modes", MainWindowExposesTranslationProviderAndDisplayModes),
    ("main window applies target translation language immediately", MainWindowAppliesTargetTranslationLanguageImmediately),
    ("main window renders translated captions below originals", MainWindowRendersTranslatedCaptionsBelowOriginals),
    ("main window throttles google translation requests", MainWindowThrottlesGoogleTranslationRequests),
    ("caption display suppresses duplicate translations", CaptionDisplaySuppressesDuplicateTranslations),
    ("main window does not switch pages for background worker updates", MainWindowDoesNotSwitchPagesForBackgroundWorkerUpdates),
    ("main window exposes large whisper model options", MainWindowExposesLargeWhisperModelOptions),
    ("main window removes live captions and engine selection", MainWindowRemovesLiveCaptionsAndEngineSelection),
    ("main window exposes whisper stt language selection", MainWindowExposesWhisperSttLanguageSelection),
    ("main window separates asr engine and model choices", MainWindowSeparatesAsrEngineAndModelChoices),
    ("main window exposes speaker count radios", MainWindowExposesSpeakerCountRadios),
    ("main window exposes speaker count mode options", MainWindowExposesSpeakerCountModeOptions),
    ("main window deselects diarization for one speaker", MainWindowDeselectsDiarizationForOneSpeaker),
    ("main window separates asr and diarization presets", MainWindowSeparatesAsrAndDiarizationPresets),
    ("main window exposes stt scenario preset radios", MainWindowExposesSttScenarioPresetRadios),
    ("main window exposes speaker range count options", MainWindowExposesSpeakerRangeCountOptions),
    ("main window exposes diarization model options", MainWindowExposesDiarizationModelOptions),
    ("main window allows asr and diarization combinations", MainWindowAllowsAsrAndDiarizationCombinations),
    ("main window exposes manual diart tuning controls", MainWindowExposesManualDiartTuningControls),
    ("model manager exposes separate hf model term buttons", ModelManagerExposesSeparateHfModelTermButtons),
    ("main window applies settings changes immediately", MainWindowAppliesSettingsChangesImmediately),
    ("main window fits settings page height to content", MainWindowFitsSettingsPageHeightToContent),
    ("main window puts display lines in overlay settings", MainWindowPutsDisplayLinesInOverlaySettings),
    ("main window separates caption and overlay line limits", MainWindowSeparatesCaptionAndOverlayLineLimits),
    ("worker environment blocks start when hf model access fails", WorkerEnvironmentBlocksStartWhenHfModelAccessFails),
    ("main window stops capture on fatal worker error", MainWindowStopsCaptureOnFatalWorkerError),
    ("worker environment skips local diarization package installs for whisperlivekit", WorkerEnvironmentSkipsLocalDiarizationPackageInstallsForWhisperLiveKit),
    ("worker protocol no longer serializes stt engine selection", WorkerProtocolNoLongerSerializesSttEngineSelection),
    ("settings store passes speaker count mode to worker configuration", SettingsStorePassesSpeakerCountModeToWorkerConfiguration),
    ("worker environment does not export stt engine to setup checks", WorkerEnvironmentDoesNotExportSttEngineToSetupChecks),
    ("settings model has no stt engine", SettingsModelHasNoSttEngine),
    ("ASR engine environment prioritizes sortformer dependency site", AsrEngineEnvironmentPrioritizesSortformerDependencySite),
    ("overlay groups captions by speaker and fades inactive speakers", OverlayGroupsCaptionsBySpeakerAndFadesInactiveSpeakers),
    ("overlay caps visible text rows per speaker", OverlayCapsVisibleTextRowsPerSpeaker),
    ("caption page caps visible text rows per speaker", CaptionPageCapsVisibleTextRowsPerSpeaker),
    ("overlay trims oversized speaker text tails before textblock clipping", OverlayTrimsOversizedSpeakerTextTailsBeforeTextBlockClipping),
    ("segmented text blocks trim tails by rendered width", SegmentedTextBlocksTrimTailsByRenderedWidth),
    ("overlay renders caption lines without trimming", OverlayRendersCaptionLinesWithoutTrimming),
    ("overlay highlights the current speaker text", OverlayHighlightsCurrentSpeakerText),
    ("overlay highlights only the newly updated caption line", OverlayHighlightsOnlyNewlyUpdatedCaptionLine),
    ("overlay keeps current batch color through non activity refresh", OverlayKeepsCurrentBatchColorThroughNonActivityRefresh),
    ("overlay supports multiple active speakers in one update batch", OverlaySupportsMultipleActiveSpeakersInOneUpdateBatch),
    ("overlay exposes configurable color settings", OverlayExposesConfigurableColorSettings),
    ("overlay batch refresh does not reset inactivity timer", OverlayBatchRefreshDoesNotResetInactivityTimer),
    ("overlay persists layout and exposes reset action", OverlayPersistsLayoutAndExposesResetAction),
    ("overlay ignores opacity events before template load completes", OverlayIgnoresOpacityEventsBeforeTemplateLoadCompletes),
    ("overlay exposes persisted click through setting", OverlayExposesPersistedClickThroughSetting),
    ("overlay open state restores on startup", OverlayOpenStateRestoresOnStartup),
    ("overlay auto sizes height with bottom anchored", OverlayAutoSizesHeightWithBottomAnchored),
    ("main window removes speaker rename feature", MainWindowRemovesSpeakerRenameFeature),
    ("worker environment installs cuda torch when nvidia gpu is present", WorkerEnvironmentInstallsCudaTorchWhenNvidiaGpuIsPresent),
    ("speaker names localize generated labels", SpeakerNamesLocalizeGeneratedLabels),
    ("setup action hints expose install action for mock mode", SetupActionHintsExposeInstallActionForMockMode),
    ("setup action hints expose token action for hf token errors", SetupActionHintsExposeTokenActionForHfTokenErrors),
    ("startup planner installs packages before preparing models", StartupPlannerInstallsPackagesBeforePreparingModels),
    ("startup planner prepares models when cached stt model cannot load", StartupPlannerPreparesModelsWhenCachedSttModelCannotLoad),
    ("startup planner allows cached local diarization without hf token", StartupPlannerAllowsCachedLocalDiarizationWithoutHfToken),
    ("startup planner requests hugging face access before local diarization", StartupPlannerRequestsHuggingFaceAccessBeforeLocalDiarization),
    ("worker environment checks hf access before setup when models need download", WorkerEnvironmentChecksHfAccessBeforeSetupWhenModelsNeedDownload),
    ("startup planner installs selected ASR engine packages", StartupPlannerInstallsSelectedAsrEnginePackages),
    ("speech separation advisor recommends models by detected hardware", SpeechSeparationAdvisorRecommendsModelsByHardware),
    ("speech separation advisor rejects unsupported runtime paths", SpeechSeparationAdvisorRejectsUnsupportedRuntimePaths),
    ("startup planner installs and prepares selected separation model", StartupPlannerInstallsAndPreparesSpeechSeparation),
    ("main window exposes automatic hardware based separation selection", MainWindowExposesAutomaticHardwareBasedSpeechSeparation),
    ("speech separation requirements only expose integrated models", SpeechSeparationRequirementsOnlyExposeIntegratedModels),
};

var failed = 0;
foreach (var test in tests)
{
    try
    {
        test.Body();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {test.Name}");
        Console.Error.WriteLine(ex);
    }
}

if (failed > 0)
{
    Console.Error.WriteLine($"{failed} test(s) failed.");
    Environment.Exit(1);
}

Console.WriteLine($"{tests.Length} test(s) passed.");

static void WorkerProtocolSerializesConfigureCommand()
{
    var command = WorkerProtocol.Configure(new WorkerConfiguration(
        InputMode.SystemAndMic,
        "small",
        Array.Empty<string>(),
        50,
        ComputeMode.Auto,
        true,
        DiarizationModel.PyannoteCommunity,
        6,
        null,
        true,
        new Dictionary<string, string> { ["speaker_2"] = "Jin" }));

    var jsonLine = WorkerProtocol.Serialize(command);

    Assert.Contains("\"type\":\"configure\"", jsonLine);
    Assert.Contains("\"inputMode\":\"system_and_mic\"", jsonLine);
    Assert.Contains("\"speaker_2\":\"Jin\"", jsonLine);
    Assert.True(jsonLine.EndsWith('\n'), "worker protocol messages must be newline delimited");
}

static void WorkerProtocolSerializesExactSpeakerCount()
{
    var command = WorkerProtocol.Configure(new WorkerConfiguration(
        InputMode.SystemAndMic,
        "large-v3-turbo",
        new[] { "ko", "en" },
        50,
        ComputeMode.Cuda,
        true,
        DiarizationModel.PyannoteCommunity,
        2,
        2,
        true,
        new Dictionary<string, string>()));

    var jsonLine = WorkerProtocol.Serialize(command);

    Assert.Contains("\"maxSpeakers\":2", jsonLine);
    Assert.Contains("\"exactSpeakers\":2", jsonLine);
    Assert.Contains("\"sttLanguages\":[\"ko\",\"en\"]", jsonLine);
}

static void WorkerProtocolSerializesSpeakerCountMode()
{
    var command = WorkerProtocol.Configure(new WorkerConfiguration(
        InputMode.SystemAndMic,
        "small",
        Array.Empty<string>(),
        100,
        ComputeMode.Auto,
        true,
        DiarizationModel.PyannoteCommunity,
        4,
        null,
        true,
        new Dictionary<string, string>(),
        SpeakerCountMode: SpeakerCountMode.ActiveMax));

    Assert.Contains("\"speakerCountMode\":\"active_max\"", WorkerProtocol.Serialize(command));
}

static void SettingsStorePassesSpeakerCountModeToWorkerConfiguration()
{
    var settings = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "Models", "AppSettings.cs"));
    var store = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "Services", "SettingsStore.cs"));

    Assert.Contains("public SpeakerCountMode SpeakerCountMode", settings);
    Assert.Contains("settings.SpeakerCountMode == SpeakerCountMode.Exact", store);
    Assert.Contains("SpeakerCountMode: settings.SpeakerCountMode", store);
}

static void WorkerProtocolSerializesDiarizationModel()
{
    var command = WorkerProtocol.Configure(new WorkerConfiguration(
        InputMode.SystemAndMic,
        "large-v3-turbo",
        new[] { "ko" },
        75,
        ComputeMode.Cuda,
        true,
        DiarizationModel.Diart,
        2,
        2,
        true,
        new Dictionary<string, string>()));

    var jsonLine = WorkerProtocol.Serialize(command);

    Assert.Contains("\"diarizationModel\":\"diart\"", jsonLine);

    var sortformerCommand = WorkerProtocol.Configure(new WorkerConfiguration(
        InputMode.SystemAndMic,
        "large-v3-turbo",
        new[] { "ko" },
        75,
        ComputeMode.Cuda,
        true,
        DiarizationModel.Sortformer,
        4,
        null,
        true,
        new Dictionary<string, string>()));

    Assert.Contains("\"diarizationModel\":\"sortformer\"", WorkerProtocol.Serialize(sortformerCommand));

}

static void WorkerProtocolSerializesSpeechSeparationModel()
{
    var command = WorkerProtocol.Configure(new WorkerConfiguration(
        InputMode.SystemAudioOnly,
        "large-v3-turbo",
        new[] { "ko", "en" },
        50,
        ComputeMode.Cuda,
        false,
        DiarizationModel.PyannoteCommunity,
        2,
        null,
        true,
        new Dictionary<string, string>(),
        SpeechSeparationModel: SpeechSeparationModel.MossFormer2));

    Assert.Contains("\"speechSeparationModel\":\"mossformer2_ss_16k\"", WorkerProtocol.Serialize(command));
}

static void WorkerProtocolSerializesAsrEngine()
{
    var configuration = new WorkerConfiguration(
        InputMode.SystemAndMic,
        "large-v3-turbo",
        new[] { "ko" },
        100,
        ComputeMode.Auto,
        true,
        DiarizationModel.PyannoteCommunity,
        4,
        null,
        true,
        new Dictionary<string, string>(),
        AsrEngine: AsrEngine.WhisperLiveKitSortformer);

    var json = WorkerProtocol.Serialize(WorkerProtocol.Configure(configuration));

    Assert.Contains("\"asrEngine\":\"whisperlivekit_sortformer\"", json);

    var defaultConfiguration = configuration with
    {
        AsrEngine = AsrEngine.None
    };

    Assert.Contains("\"asrEngine\":\"faster_whisper\"", WorkerProtocol.Serialize(WorkerProtocol.Configure(defaultConfiguration)));

    var whisperXConfiguration = configuration with
    {
        AsrEngine = AsrEngine.WhisperX
    };

    Assert.Contains("\"asrEngine\":\"whisperx\"", WorkerProtocol.Serialize(WorkerProtocol.Configure(whisperXConfiguration)));
}

static void WorkerProtocolSerializesManualDiartTuning()
{
    var command = WorkerProtocol.Configure(new WorkerConfiguration(
        InputMode.SystemAndMic,
        "large-v3-turbo",
        new[] { "ko" },
        100,
        ComputeMode.Auto,
        true,
        DiarizationModel.Diart,
        4,
        null,
        true,
        new Dictionary<string, string>(),
        true,
        8.0,
        0.25,
        0.8,
        0.576,
        0.915,
        0.648,
        DiarizationQualityPreset: 100));

    var jsonLine = WorkerProtocol.Serialize(command);

    Assert.Contains("\"diartManualSettings\":true", jsonLine);
    Assert.Contains("\"diartDurationSeconds\":8", jsonLine);
    Assert.Contains("\"diartStepSeconds\":0.25", jsonLine);
    Assert.Contains("\"diartLatencySeconds\":0.8", jsonLine);
    Assert.Contains("\"diartTauActive\":0.576", jsonLine);
    Assert.Contains("\"diartRhoUpdate\":0.915", jsonLine);
    Assert.Contains("\"diartDeltaNew\":0.648", jsonLine);
}

static void WorkerProtocolParsesFinalCaption()
{
    const string json = """
        {"type":"final_caption","speakerId":"speaker_2","text":"hello there","startMs":1200,"endMs":2400,"latencyMs":371}
        """;

    var message = WorkerProtocol.ParseEvent(json);
    var caption = Assert.IsType<FinalCaptionEvent>(message);

    Assert.Equal("speaker_2", caption.SpeakerId);
    Assert.Equal("hello there", caption.Text);
    Assert.Equal(1200, caption.StartMs);
    Assert.Equal(2400, caption.EndMs);
    Assert.Equal(371, caption.LatencyMs);
}

static void SpeakerNamesPreferManualRename()
{
    var names = new SpeakerNameMap();
    names.Rename("speaker_2", "Lilian");

    Assert.Equal("You", names.DisplayName("mic"));
    Assert.Equal("Lilian", names.DisplayName("speaker_2"));
    Assert.Equal("Speaker 3", names.DisplayName("speaker_3"));
}

static void SpeakerNamesDisplayUnknownSpeaker()
{
    var names = new SpeakerNameMap(ResolvedAppLanguage.English);
    Assert.Equal("Unknown speaker", names.DisplayName("speaker_unknown"));

    var koreanNames = new SpeakerNameMap(ResolvedAppLanguage.Korean);
    Assert.Equal("알 수 없는 화자", koreanNames.DisplayName("speaker_unknown"));
}

static void SpeakerNamesLocalizeGeneratedLabels()
{
    var names = new SpeakerNameMap(ResolvedAppLanguage.Korean);

    Assert.Equal("나", names.DisplayName("mic"));
    Assert.Equal("화자 3", names.DisplayName("speaker_3"));
}

static void CaptionMergerOverwritesPartial()
{
    var merger = new CaptionMerger(maxEntries: 3);

    merger.Apply(new PartialCaptionEvent("speaker_2", "hel", 0, 400, null));
    merger.Apply(new PartialCaptionEvent("speaker_2", "hello", 0, 700, 140));
    merger.Apply(new FinalCaptionEvent("speaker_2", "hello there", 0, 1300, 250));
    merger.Apply(new FinalCaptionEvent("speaker_3", "reply", 1400, 2000, null));

    var entries = merger.Entries.ToArray();
    Assert.Equal(2, entries.Length);
    Assert.Equal("hello there", entries[0].Text);
    Assert.True(entries[0].IsFinal, "final caption must replace the latest partial for the same speaker");
    Assert.Equal("reply", entries[1].Text);
}

static void CaptionMergerPreservesPartialIdWhileSameSpeakerTextUpdates()
{
    var merger = new CaptionMerger(maxEntries: 3);

    var first = merger.Apply(new PartialCaptionEvent("speaker_1", "집에 이제", 0, 1000, null));
    var second = merger.Apply(new PartialCaptionEvent("speaker_1", "집에 이제 시집을 갔어요.", 0, 1500, null));

    Assert.Equal(1, merger.Entries.Count);
    Assert.Equal(first!.Id, second!.Id);
    Assert.Equal("집에 이제 시집을 갔어요.", merger.Entries[0].Text);
}

static void CaptionMergerAppendsAdjacentFinalTextFromSameSpeaker()
{
    var merger = new CaptionMerger(maxEntries: 3);

    merger.Apply(new FinalCaptionEvent("speaker_1", "간단하게", 0, 1000, 100));
    var merged = merger.Apply(new FinalCaptionEvent("speaker_1", "소리 내고", 1200, 2200, 120));

    Assert.Equal(1, merger.Entries.Count);
    Assert.Equal("간단하게 소리 내고", merger.Entries[0].Text);
    Assert.Equal(merger.Entries[0], merged);
}

static void CaptionMergerAppendsSameSpeakerAfterInterleavedEvent()
{
    var merger = new CaptionMerger(maxEntries: 3);

    var first = merger.Apply(new FinalCaptionEvent("speaker_1", "첫 문장", 0, 1000, 100));
    merger.Apply(new FinalCaptionEvent("speaker_2", "다른 화자", 1100, 1300, 100));
    var merged = merger.Apply(new FinalCaptionEvent("speaker_1", "다음 문장", 1500, 2200, 100));

    Assert.Equal(first!.Id, merged!.Id);
    Assert.Equal(2, merger.Entries.Count);
    Assert.Equal("speaker_2,speaker_1", string.Join(",", merger.Entries.Select(entry => entry.SpeakerId)));
    Assert.Equal("첫 문장 다음 문장", merger.Entries[1].Text);
}

static void CaptionMergerKeepsDisplayLineCountPerSpeaker()
{
    var merger = new CaptionMerger(maxEntries: 2);

    merger.Apply(new FinalCaptionEvent("speaker_1", "a1", 0, 100, null));
    merger.Apply(new FinalCaptionEvent("speaker_2", "b1", 3000, 3100, null));
    merger.Apply(new FinalCaptionEvent("speaker_1", "a2", 6000, 6100, null));
    merger.Apply(new FinalCaptionEvent("speaker_2", "b2", 9000, 9100, null));
    merger.Apply(new FinalCaptionEvent("speaker_1", "a3", 12000, 12100, null));

    Assert.Equal(4, merger.Entries.Count);
    Assert.Equal("speaker_2,speaker_1,speaker_2,speaker_1", string.Join(",", merger.Entries.Select(entry => entry.SpeakerId)));
    Assert.Equal("b1,a2,b2,a3", string.Join(",", merger.Entries.Select(entry => entry.Text)));
}

static void SpeakerSegmentTimelineChoosesDominantOverlap()
{
    var timeline = new SpeakerSegmentTimeline();

    timeline.Add(new SpeakerSegmentEvent("speaker_1", 0, 4000, 0));
    timeline.Add(new SpeakerSegmentEvent("speaker_2", 3500, 6500, 0));

    var speaker = timeline.ResolveSpeaker(startMs: 3800, endMs: 6200, fallbackSpeakerId: "speaker_1");

    Assert.Equal("speaker_2", speaker);
}

static void SpeakerSegmentTimelineFallsBackToLatestNearbySpeaker()
{
    var timeline = new SpeakerSegmentTimeline();

    timeline.Add(new SpeakerSegmentEvent("speaker_1", 0, 1000, 0));
    timeline.Add(new SpeakerSegmentEvent("speaker_2", 2500, 3500, 0));

    var speaker = timeline.ResolveSpeaker(startMs: 3800, endMs: 4600, fallbackSpeakerId: "speaker_1");

    Assert.Equal("speaker_2", speaker);
}

static void HistoryStoreRoundTripsFinalCaptionEntries()
{
    var dbPath = Path.Combine(Path.GetTempPath(), $"live-dialogue-translator-test-{Guid.NewGuid():N}.jsonl");
    try
    {
        var store = new CaptionHistoryStore(dbPath);
        var entry = new CaptionEntry(
            Guid.NewGuid(),
            "speaker_2",
            "Lilian",
            "sample text",
            100,
            900,
            123,
            DateTimeOffset.Parse("2026-05-13T05:00:00Z"),
            IsFinal: true);

        store.Append(entry);

        var loaded = store.LoadRecent(5).Single();
        Assert.Equal(entry.Id, loaded.Id);
        Assert.Equal("speaker_2", loaded.SpeakerId);
        Assert.Equal("Lilian", loaded.SpeakerName);
        Assert.Equal("sample text", loaded.Text);
        Assert.Equal(123, loaded.LatencyMs);
        Assert.True(loaded.IsFinal, "history must persist final state");
    }
    finally
    {
        if (File.Exists(dbPath))
        {
            File.Delete(dbPath);
        }
    }
}

static void HistoryStoreUpsertsMergedCaptionEntries()
{
    var dbPath = Path.Combine(Path.GetTempPath(), $"live-dialogue-translator-test-{Guid.NewGuid():N}.jsonl");
    try
    {
        var store = new CaptionHistoryStore(dbPath);
        var id = Guid.NewGuid();
        store.Append(new CaptionEntry(
            id,
            "speaker_2",
            "Speaker 2",
            "first",
            0,
            1000,
            200,
            DateTimeOffset.Parse("2026-05-13T05:00:00Z"),
            IsFinal: true));

        store.Append(new CaptionEntry(
            id,
            "speaker_2",
            "Speaker 2",
            "first second",
            0,
            1800,
            250,
            DateTimeOffset.Parse("2026-05-13T05:00:01Z"),
            IsFinal: true));

        var loaded = store.LoadRecent(5);
        Assert.Equal(1, loaded.Count);
        Assert.Equal("first second", loaded[0].Text);
        Assert.Equal(1800, loaded[0].EndMs);
    }
    finally
    {
        if (File.Exists(dbPath))
        {
            File.Delete(dbPath);
        }
    }
}

static void HistoryStoreCanClearEntries()
{
    var dbPath = Path.Combine(Path.GetTempPath(), $"live-dialogue-translator-test-{Guid.NewGuid():N}.jsonl");
    try
    {
        var store = new CaptionHistoryStore(dbPath);
        store.Append(new CaptionEntry(
            Guid.NewGuid(),
            "speaker_1",
            "Speaker 1",
            "hello",
            0,
            1000,
            null,
            DateTimeOffset.UtcNow,
            IsFinal: true));

        store.Clear();

        Assert.Equal(0, store.LoadRecent(10).Count);
    }
    finally
    {
        if (File.Exists(dbPath))
        {
            File.Delete(dbPath);
        }
    }
}

static void HuggingFaceTokenLinkPointsToAccessTokenSettings()
{
    Assert.Equal("https://huggingface.co/settings/tokens", HuggingFaceLinks.AccessTokensUrl);
    Assert.Equal("https://huggingface.co/pyannote/speaker-diarization-community-1", HuggingFaceLinks.CommunityModelUrl);
}

static void ProjectLinksExposeReferenceAndModelPages()
{
    Assert.Equal("https://github.com/Me-in-U/LiveDialogue-Translator", ProjectLinks.ProjectUrl);
    Assert.Equal("https://github.com/SakiRinn/LiveCaptions-Translator", ProjectLinks.ReferenceProjectUrl);
    Assert.Equal("https://github.com/SYSTRAN/faster-whisper", ProjectLinks.FasterWhisperUrl);
    Assert.Equal("https://github.com/pyannote/pyannote-audio", ProjectLinks.PyannoteAudioUrl);
    Assert.Equal("https://www.apache.org/licenses/LICENSE-2.0", ProjectLinks.LicenseUrl);
}

static void ProjectLinksExposeAllSpeechBackendPages()
{
    Assert.Equal("https://huggingface.co/Qwen/Qwen3-ASR-1.7B", ProjectLinks.QwenAsrUrl);
    Assert.Equal("https://github.com/QuentinFuxa/WhisperLiveKit", ProjectLinks.WhisperLiveKitUrl);
    Assert.Equal("https://github.com/m-bain/whisperX", ProjectLinks.WhisperXUrl);
    Assert.Equal("https://github.com/juanmc2005/diart", ProjectLinks.DiartUrl);
    Assert.Equal("https://huggingface.co/nvidia/diar_streaming_sortformer_4spk-v2", ProjectLinks.SortformerUrl);
    Assert.Equal("https://huggingface.co/alibabasglab/MossFormer2_SS_16K", ProjectLinks.MossFormer2Url);
    Assert.Equal("https://huggingface.co/speechbrain/sepformer-whamr16k", ProjectLinks.SepFormerUrl);
}

static void InstallerShowsApacheLicense()
{
    var installer = File.ReadAllText(Path.Combine("installer", "LiveDialogueTranslator.iss"));
    var license = File.ReadAllText("LICENSE");

    Assert.Contains("LicenseFile=..\\LICENSE", installer);
    Assert.Contains("Apache License", license);
    Assert.Contains("Version 2.0", license);
}

static void InstallerUsesKoreanWizardLanguage()
{
    var installer = File.ReadAllText(Path.Combine("installer", "LiveDialogueTranslator.iss"));

    Assert.Contains("[Languages]", installer);
    Assert.Contains("Name: \"korean\"; MessagesFile: \"compiler:Languages\\Korean.isl\"", installer);
    Assert.Contains("Description: \"바탕 화면 바로 가기 만들기\"", installer);
    Assert.Contains("GroupDescription: \"추가 아이콘:\"", installer);
    Assert.Contains("Description: \"{#MyAppName} 실행\"", installer);
}

static void ReleaseVersionIs110()
{
    var props = File.ReadAllText("Directory.Build.props");
    var installer = File.ReadAllText(Path.Combine("installer", "LiveDialogueTranslator.iss"));

    Assert.Contains("<Version>1.1.0</Version>", props);
    Assert.Contains("#define MyAppVersion \"1.1.0\"", installer);
}

static void MainWindowExposesProjectAndLicenseInfoLinks()
{
    var xaml = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml"));
    var source = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml.cs"));
    var localizer = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "Services", "Localizer.cs"));

    Assert.Contains("InfoProjectLabelRun", xaml);
    Assert.Contains("ProjectLink_Click", xaml);
    Assert.Contains("InfoLicenseLabelRun", xaml);
    Assert.Contains("LicenseLink_Click", xaml);
    Assert.Contains("Apache-2.0", xaml);
    Assert.Contains("ApplyInfoLocalization();", source);
    Assert.Contains("OpenExternalLink(ProjectLinks.ProjectUrl)", source);
    Assert.Contains("OpenExternalLink(ProjectLinks.LicenseUrl)", source);
    Assert.Contains("[\"InfoLinks\"]", localizer);
    Assert.Contains("[\"Project\"]", localizer);
    Assert.Contains("[\"License\"]", localizer);
}

static void MainWindowInfoPageListsAllSpeechBackends()
{
    var xaml = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml"));
    var source = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml.cs"));
    var localizer = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "Services", "Localizer.cs"));
    var readme = File.ReadAllText("README.md");

    Assert.Contains("InfoAsrLabelRun", xaml);
    Assert.Contains("InfoDiarizationLabelRun", xaml);
    Assert.Contains("InfoSpeechSeparationLabelRun", xaml);
    Assert.Contains("FasterWhisperLink_Click", xaml);
    Assert.Contains("QwenAsrLink_Click", xaml);
    Assert.Contains("WhisperLiveKitLink_Click", xaml);
    Assert.Contains("WhisperXLink_Click", xaml);
    Assert.Contains("PyannoteLink_Click", xaml);
    Assert.Contains("DiartLink_Click", xaml);
    Assert.Contains("SortformerLink_Click", xaml);
    Assert.Contains("MossFormer2Link_Click", xaml);
    Assert.Contains("SepFormerLink_Click", xaml);
    Assert.Contains("OpenExternalLink(ProjectLinks.QwenAsrUrl)", source);
    Assert.Contains("OpenExternalLink(ProjectLinks.WhisperLiveKitUrl)", source);
    Assert.Contains("OpenExternalLink(ProjectLinks.WhisperXUrl)", source);
    Assert.Contains("OpenExternalLink(ProjectLinks.DiartUrl)", source);
    Assert.Contains("OpenExternalLink(ProjectLinks.SortformerUrl)", source);
    Assert.Contains("OpenExternalLink(ProjectLinks.MossFormer2Url)", source);
    Assert.Contains("OpenExternalLink(ProjectLinks.SepFormerUrl)", source);
    Assert.Contains("[\"SupportedAsrBackends\"]", localizer);
    Assert.Contains("[\"SupportedDiarizationBackends\"]", localizer);
    Assert.Contains("[\"SupportedSpeechSeparationBackends\"]", localizer);
    Assert.True(!localizer.Contains("(\"DIART (Diarization Model)\"", StringComparison.Ordinal), "Diarization group title must not imply Diart is the only diarization backend.");
    Assert.Contains("Qwen3-ASR", readme);
    Assert.Contains("WhisperLiveKit", readme);
    Assert.Contains("WhisperX", readme);
    Assert.Contains("Diart", readme);
    Assert.Contains("Sortformer", readme);
    Assert.Contains("MossFormer2_SS_16K", readme);
    Assert.Contains("SepFormer WHAMR16k", readme);
}

static void HuggingFaceTokenGuidanceNamesRequiredPermission()
{
    Assert.Contains("Read access to contents of all public gated repos you can access", HuggingFaceLinks.TokenPermissionSummary);
    Assert.Contains("No Write", HuggingFaceLinks.TokenPermissionSummary);
    Assert.Contains("Inference", HuggingFaceLinks.TokenPermissionSummary);
}

static void LanguageResolverFollowsWindowsUiCulture()
{
    Assert.Equal(ResolvedAppLanguage.Korean, AppLanguageResolver.Resolve(AppLanguage.Auto, "ko-KR"));
    Assert.Equal(ResolvedAppLanguage.English, AppLanguageResolver.Resolve(AppLanguage.Auto, "en-US"));
    Assert.Equal(ResolvedAppLanguage.Korean, AppLanguageResolver.Resolve(AppLanguage.Korean, "en-US"));
    Assert.Equal(ResolvedAppLanguage.English, AppLanguageResolver.Resolve(AppLanguage.English, "ko-KR"));
}

static void PythonRuntimeLayoutUsesDownloadedAppManagedPython()
{
    var runtimeRoot = Path.Combine("C:", "Users", "Zoe", "AppData", "Local", "LiveDialogue Translator", "runtime");
    var pythonDirectory = Path.Combine(runtimeRoot, "python-3.11.9");

    Assert.Equal(
        pythonDirectory,
        PythonRuntimeLayout.PythonDirectory(runtimeRoot));
    Assert.Equal(
        Path.Combine(pythonDirectory, "python.exe"),
        PythonRuntimeLayout.PythonExecutablePath(runtimeRoot));
    Assert.Equal(
        Path.Combine(runtimeRoot, "downloads", "python-3.11.9-embed-amd64.zip"),
        PythonRuntimeLayout.RuntimeArchivePath(runtimeRoot));
    Assert.Equal(
        Path.Combine(runtimeRoot, "downloads", "get-pip.py"),
        PythonRuntimeLayout.GetPipPath(runtimeRoot));
    Assert.Equal(
        "https://www.python.org/ftp/python/3.11.9/python-3.11.9-embed-amd64.zip",
        PythonRuntimeLayout.DownloadUrl);
    Assert.Equal(
        "https://bootstrap.pypa.io/get-pip.py",
        PythonRuntimeLayout.GetPipUrl);
}

static void PythonPipCommandsSuppressScriptLocationWarnings()
{
    var requirementsPath = Path.Combine("C:", "Program Files", "LiveDialogue Translator", "worker", "requirements.txt");
    var getPipPath = Path.Combine("C:", "Users", "Zoe", "AppData", "Local", "LiveDialogue Translator", "runtime", "downloads", "get-pip.py");

    Assert.Contains("--no-warn-script-location", PythonPipCommands.UpgradePipArguments());
    Assert.Contains("--no-warn-script-location", PythonPipCommands.InstallRequirementsArguments(requirementsPath));
    Assert.Contains($"-r \"{requirementsPath}\"", PythonPipCommands.InstallRequirementsArguments(requirementsPath));
    Assert.Contains("--target", PythonPipCommands.InstallRequirementsToTargetArguments(requirementsPath, Path.Combine("C:", "target")));
    Assert.Contains("--no-warn-script-location", PythonPipCommands.BootstrapPipArguments(getPipPath));
}

static void PythonPipCommandsInstallCudaTorchFromCu128Index()
{
    var arguments = PythonPipCommands.InstallCudaTorchArguments();

    Assert.Contains("--index-url https://download.pytorch.org/whl/cu128", arguments);
    Assert.Contains("torch==2.11.0+cu128", arguments);
    Assert.Contains("torchaudio==2.11.0+cu128", arguments);
    Assert.Contains("--upgrade", arguments);
}

static void PythonPipCommandsInstallDiartWithoutDependencyResolverConflict()
{
    var arguments = PythonPipCommands.InstallDiartArguments();

    Assert.Contains("diart==0.9.2", arguments);
    Assert.Contains("--no-deps", arguments);
    Assert.Contains("--no-warn-script-location", arguments);
}

static void WorkerRequirementsPinPyannoteBeforeTorchcodecDependencyLine()
{
    var requirements = File.ReadAllText(Path.Combine("worker", "requirements.txt"));

    Assert.Contains("pyannote.audio==4.0.4", requirements);
    Assert.Contains("torch==2.11.0", requirements);
    Assert.Contains("torchaudio==2.11.0", requirements);
    Assert.Contains("torchcodec==0.11.1", requirements);
    Assert.Contains("huggingface-hub==1.14.0", requirements);
    Assert.True(!requirements.Contains("pyannote.audio>=", StringComparison.Ordinal), "pyannote.audio must stay aligned with the Community-1 model API.");
    Assert.True(!requirements.Contains("torch>=", StringComparison.Ordinal), "torch must stay aligned with torchaudio and torchcodec.");
    Assert.True(!requirements.Contains("torchaudio>=", StringComparison.Ordinal), "torchaudio must stay aligned with torch.");
    Assert.True(!requirements.Contains("huggingface-hub>=", StringComparison.Ordinal), "huggingface-hub must stay aligned with pyannote.audio 4.x.");
    var qwenRequirements = File.ReadAllText(Path.Combine("worker", "requirements-qwen3-asr.txt"));
    var qwenEnv = File.ReadAllText(Path.Combine("worker", "env", "qwen3-asr.env"));
    var wlkRequirements = File.ReadAllText(Path.Combine("worker", "requirements-whisperlivekit-sortformer.txt"));
    Assert.Contains("qwen-asr==0.0.6", qwenRequirements);
    Assert.Contains("Qwen/Qwen3-ASR-1.7B", qwenEnv);
    Assert.Contains("Qwen/Qwen3-ASR-0.6B", qwenEnv);
    Assert.Contains("Qwen/Qwen3-ForcedAligner-0.6B", qwenEnv);
    Assert.Contains("whisperlivekit", wlkRequirements);
    Assert.Contains("diarization-sortformer", wlkRequirements);
}

static void PythonProcessEnvironmentUsesUtf8AndPlainPipOutput()
{
    var environment = new Dictionary<string, string?>();

    PythonProcessEnvironment.Apply(environment);

    Assert.Equal("1", environment["PYTHONUTF8"]);
    Assert.Equal("utf-8", environment["PYTHONIOENCODING"]);
    Assert.Equal("1", environment["PYTHONNOUSERSITE"]);
    Assert.Equal("1", environment["PIP_NO_COLOR"]);
    Assert.Equal("1", environment["PIP_DISABLE_PIP_VERSION_CHECK"]);
}

static void WorkerStderrClassifierIgnoresBenignGpuLibraryWarnings()
{
    Assert.True(
        WorkerStderrClassifier.ShouldIgnore("W0513 17:23:53.976000 20916 Lib\\site-packages\\torch\\utils\\flop_counter.py:29] triton not found; flop counting will not work for triton kernels"),
        "torch triton flop-counter warning should not be shown as a worker error.");
    Assert.True(
        WorkerStderrClassifier.ShouldIgnore("C:\\runtime\\Lib\\site-packages\\pyannote\\audio\\utils\\reproducibility.py:74: ReproducibilityWarning: TensorFloat-32 (TF32) has been disabled as it might lead to reproducibility issues and lower accuracy."),
        "pyannote TF32 reproducibility warning should not be shown as a worker error.");
    Assert.True(
        WorkerStderrClassifier.ShouldIgnore("   >>> torch.backends.cuda.matmul.allow_tf32 = True"),
        "pyannote TF32 instruction lines should not be shown as worker errors.");
    Assert.True(
        WorkerStderrClassifier.ShouldIgnore("It can be re-enabled by calling"),
        "pyannote TF32 explanatory lines should not be shown as worker errors.");
    Assert.True(
        WorkerStderrClassifier.ShouldIgnore("  warnings.warn("),
        "pyannote warning implementation line should not be shown as a worker error.");
    Assert.True(
        WorkerStderrClassifier.ShouldIgnore("See https://github.com/pyannote/pyannote-audio/issues/1370 for more details."),
        "pyannote TF32 reference lines should not be shown as worker errors.");
    Assert.True(
        WorkerStderrClassifier.ShouldIgnore("UserWarning: std(): degrees of freedom is <= 0."),
        "short pyannote pooling windows should not be shown as worker errors.");
    Assert.True(
        WorkerStderrClassifier.ShouldIgnore("RuntimeWarning: Mean of empty slice"),
        "empty diarization windows should not be shown as worker errors.");
    Assert.True(
        WorkerStderrClassifier.ShouldIgnore("RuntimeWarning: invalid value encountered in divide"),
        "numpy empty-window warnings should not be shown as worker errors.");
    Assert.True(
        WorkerStderrClassifier.ShouldIgnore("  std = sequences.std(dim=-1, correction=1)"),
        "pyannote warning source lines should not be shown as worker errors.");
    Assert.True(
        WorkerStderrClassifier.ShouldIgnore("  return _methods._mean(a, axis=axis, dtype=dtype,"),
        "numpy warning source lines should not be shown as worker errors.");
    Assert.True(
        WorkerStderrClassifier.ShouldIgnore("  ret = um.true_divide("),
        "numpy warning source lines should not be shown as worker errors.");
    Assert.True(
        WorkerStderrClassifier.ShouldIgnore("Lightning automatically upgraded your loaded checkpoint from v1.5.4 to v2.6.1."),
        "lightning checkpoint compatibility notices should not be shown as worker errors.");
    Assert.True(
        WorkerStderrClassifier.ShouldIgnore("Redirecting import of pytorch_lightning.callbacks.early_stopping.EarlyStopping to lightning.pytorch.callbacks.early_stopping.EarlyStopping"),
        "lightning checkpoint import redirects should not be shown as worker errors.");
    Assert.True(
        WorkerStderrClassifier.ShouldIgnore("Found keys that are not in the model state dict but in the checkpoint: ['loss_func.W']"),
        "lightning checkpoint load compatibility notices should not be shown as worker errors.");
    Assert.True(
        !WorkerStderrClassifier.ShouldIgnore("Traceback (most recent call last):"),
        "real tracebacks must still be shown.");
}

static void WorkerClientFiltersBenignStderrWarnings()
{
    var workerClient = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "Services", "WorkerClient.cs"));
    var modelManager = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "ModelManagerWindow.xaml.cs"));

    Assert.Contains("WorkerStderrClassifier.ShouldIgnore(message)", workerClient);
    Assert.Contains("FilterBenignStderr(await process.StandardError.ReadToEndAsync())", modelManager);
}

static void AppPublishIncludesWorkerSupportModules()
{
    var project = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "LiveDialogueTranslator.App.csproj"));

    Assert.Contains("..\\..\\worker\\speaker_worker.py", project);
    Assert.Contains("..\\..\\worker\\diarization_state.py", project);
}

static void CaptionPageUsesOverlayStyleSpeakerCard()
{
    var xaml = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml"));
    var source = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml.cs"));

    Assert.Contains("x:Name=\"CaptionSpeakerItems\"", xaml);
    Assert.True(!xaml.Contains("ItemsSource=\"{Binding Lines}\"", StringComparison.Ordinal), "caption page must render grouped original and translation blocks rather than alternating per-line pairs.");
    Assert.Contains("local:SegmentedTextBlockBehavior.Segments=\"{Binding OriginalSegments}\"", xaml);
    Assert.Contains("local:SegmentedTextBlockBehavior.Segments=\"{Binding TranslationSegments}\"", xaml);
    Assert.Contains("x:Name=\"DebugStateText\"", xaml);
    Assert.Contains("x:Name=\"DetailStatusText\"", xaml);
    Assert.Contains("x:Name=\"StatusText\"", xaml);
    Assert.Contains("Grid.Row=\"1\"", xaml);
    Assert.Contains("Grid.Row=\"2\"", xaml);
    Assert.Contains("TextWrapping=\"Wrap\"", xaml);
    Assert.True(!xaml.Contains("DetailCaptionText", StringComparison.Ordinal), "caption page should not keep a separate text log because Python logs live on the console page.");
    Assert.True(!xaml.Contains("DetailTitleText", StringComparison.Ordinal), "caption page should be one overlay-style card, not a caption card plus a log title.");
    Assert.Contains("private readonly ObservableCollection<OverlaySpeakerViewModel> captionSpeakers", source);
    Assert.Contains("CaptionSpeakerItems.ItemsSource = captionSpeakers;", source);
    Assert.Contains("RefreshCaptionSpeakers", source);
    Assert.Contains("SetCaptionPlaceholder", source);
    Assert.Contains("ModelLine", source);
    Assert.Contains("LogLine", source);
}

static void CaptionPageRemovesInactiveSpeakers()
{
    var source = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml.cs"));

    Assert.Contains("private static readonly TimeSpan CaptionInactiveTimeout", source);
    Assert.Contains("private static readonly TimeSpan CaptionFadeDuration", source);
    Assert.Contains("private readonly DispatcherTimer captionInactivityTimer", source);
    Assert.Contains("captionInactivityTimer.Tick += CaptionInactivityTimer_Tick;", source);
    Assert.Contains("captionInactivityTimer.Start();", source);
    Assert.Contains("CaptionInactivityTimer_Tick", source);
    Assert.Contains("now - speaker.LastUpdatedUtc > CaptionInactiveTimeout", source);
    Assert.Contains("speaker.BeginFade(now);", source);
    Assert.Contains("now - speaker.FadeStartedUtc.Value > CaptionFadeDuration", source);
    Assert.Contains("captionSpeakers.Remove(speaker);", source);
    Assert.Contains("captionInactivityTimer.Stop();", source);
}

static void CaptionSpeakerLabelColumnsStayCompact()
{
    var mainXaml = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml"));
    var overlayXaml = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "OverlayWindow.xaml"));

    Assert.Contains("<ColumnDefinition Width=\"96\" />", mainXaml);
    Assert.Contains("<ColumnDefinition Width=\"96\" />", overlayXaml);
    Assert.True(!mainXaml.Contains("<ColumnDefinition Width=\"122\" />", StringComparison.Ordinal), "current caption speaker column should not leave a wide gap before text.");
    Assert.True(!overlayXaml.Contains("<ColumnDefinition Width=\"120\" />", StringComparison.Ordinal), "overlay speaker column should not leave a wide gap before text.");
}

static void WorkerClientExportsHuggingFaceTokenToCaptureWorker()
{
    var source = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "Services", "WorkerClient.cs"));

    Assert.Contains("string? huggingFaceToken", source);
    Assert.Contains("psi.Environment[\"HF_TOKEN\"] = huggingFaceToken;", source);
}

static void MainWindowPassesSavedHuggingFaceTokenWhenStartingCapture()
{
    var source = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml.cs"));

    Assert.Contains("workerClient.StartAsync(workerConfiguration, settings.HuggingFaceToken)", source);
}

static void MainWindowPreservesSavedSttModelWhenComboSelectionIsEmpty()
{
    var source = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml.cs"));

    Assert.Contains("settings.SttModel = SelectedContent(SttModelBox, settings.SttModel);", source);
    Assert.True(!source.Contains("settings.SttModel = SelectedContent(SttModelBox, \"small\");", StringComparison.Ordinal), "starting capture must not silently reset the selected STT model to small.");
}

static void WorkerClientRestartsExistingWorkerBeforeApplyingNewConfiguration()
{
    var source = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "Services", "WorkerClient.cs"));

    Assert.Contains("await StopAsync(token);", source);
    Assert.True(!source.Contains("await SendAsync(WorkerProtocol.Configure(configuration), token);\r\n            await SendAsync(WorkerProtocol.Start(), token);\r\n            return;", StringComparison.Ordinal), "existing workers should not keep an already loaded model after settings change.");
}

static void WorkerClientWritesStdinWithoutUtf8Bom()
{
    var source = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "Services", "WorkerClient.cs"));

    Assert.Contains("new(encoderShouldEmitUTF8Identifier: false)", source);
    Assert.True(!source.Contains("StandardInputEncoding = Encoding.UTF8", StringComparison.Ordinal), "worker stdin must not emit a UTF-8 BOM before the first configure command.");
}

static void WorkerClientExposesRawPythonLogs()
{
    var source = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "Services", "WorkerClient.cs"));

    Assert.Contains("public event EventHandler<WorkerLogLine>? LogReceived;", source);
    Assert.Contains("LogReceived?.Invoke(this, new WorkerLogLine(\"stdout\", line));", source);
    Assert.Contains("LogReceived?.Invoke(this, new WorkerLogLine(\"stderr\", message));", source);
    Assert.Contains("if (WorkerStderrClassifier.ShouldIgnore(message))", source);
    Assert.Contains("continue;", source);
}

static void WorkerEnvironmentExposesSetupPythonLogs()
{
    var source = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "Services", "WorkerEnvironmentService.cs"));
    var mainWindow = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml.cs"));

    Assert.Contains("public event EventHandler<WorkerLogLine>? LogReceived;", source);
    Assert.Contains("LogReceived?.Invoke(this, new WorkerLogLine(stream, line));", source);
    Assert.Contains("ReadLinesAsync(process.StandardOutput, stdout, \"stdout\", progressTitle, token)", source);
    Assert.Contains("ReadLinesAsync(process.StandardError, stderr, \"stderr\", progressTitle, token)", source);
    Assert.Contains("workerEnvironment.LogReceived += WorkerEnvironment_LogReceived;", mainWindow);
    Assert.Contains("private void WorkerEnvironment_LogReceived(object? sender, WorkerLogLine e)", mainWindow);
}

static void MainWindowPreservesConsoleScrollWhileReviewingLogs()
{
    var source = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml.cs"));

    Assert.Contains("var shouldAutoScroll = IsConsoleKeepBottomEnabled() || IsConsoleScrolledToBottom();", source);
    Assert.Contains("var previousOffset = ConsoleTextBox.VerticalOffset;", source);
    Assert.Contains("if (shouldAutoScroll)", source);
    Assert.Contains("ConsoleTextBox.ScrollToEnd();", source);
    Assert.Contains("ConsoleTextBox.ScrollToVerticalOffset(previousOffset);", source);
    Assert.Contains("private bool IsConsoleScrolledToBottom()", source);
}

static void MainWindowWrapsConsoleLogsAndExposesConsoleControls()
{
    var xaml = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml"));
    var source = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml.cs"));
    var localizer = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "Services", "Localizer.cs"));
    var marker = "x:Name=\"ConsoleTextBox\"";
    var start = xaml.IndexOf(marker, StringComparison.Ordinal);
    Assert.True(start >= 0, "MainWindow.xaml must contain ConsoleTextBox.");
    var snippet = xaml.Substring(start, Math.Min(500, xaml.Length - start));

    Assert.Contains("x:Name=\"ConsoleClearButton\"", xaml);
    Assert.Contains("Click=\"ConsoleClearButton_Click\"", xaml);
    Assert.Contains("x:Name=\"ConsoleKeepBottomButton\"", xaml);
    Assert.Contains("Style=\"{StaticResource ConsoleKeepBottomButtonStyle}\"", xaml);
    Assert.Contains("x:Key=\"ConsoleKeepBottomButtonStyle\"", xaml);
    Assert.Contains("<Trigger Property=\"IsChecked\" Value=\"True\">", xaml);
    Assert.Contains("Click=\"ConsoleKeepBottomButton_Click\"", xaml);
    Assert.Contains("TextWrapping=\"Wrap\"", snippet);
    Assert.Contains("HorizontalScrollBarVisibility=\"Disabled\"", snippet);
    Assert.Contains("private void ConsoleClearButton_Click", source);
    Assert.Contains("ClearPythonConsole();", source);
    Assert.Contains("private void ConsoleKeepBottomButton_Click", source);
    Assert.Contains("private bool IsConsoleKeepBottomEnabled()", source);
    Assert.Contains("IsConsoleKeepBottomEnabled() || IsConsoleScrolledToBottom()", source);
    Assert.Contains("ConsoleTextBox.ScrollToEnd();", source);
    Assert.Contains("[\"ClearConsoleLogs\"]", localizer);
    Assert.Contains("[\"KeepConsoleAtBottom\"]", localizer);
}

static void MainWindowClearsPythonConsoleWhenModelSelectionChanges()
{
    var source = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml.cs"));
    var marker = "private static string BuildPythonConsoleModelKey(AppSettings settings)";
    var start = source.IndexOf(marker, StringComparison.Ordinal);
    Assert.True(start >= 0, "MainWindow.xaml.cs must have a model key for Python console reset decisions.");
    var snippet = source.Substring(start, Math.Min(600, source.Length - start));

    Assert.Contains("private string pythonConsoleModelKey = string.Empty;", source);
    Assert.Contains("pythonConsoleModelKey = BuildPythonConsoleModelKey(settings);", source);
    Assert.Contains("var previousPythonConsoleModelKey = pythonConsoleModelKey;", source);
    Assert.Contains("ClearPythonConsoleIfModelChanged(previousPythonConsoleModelKey);", source);
    Assert.Contains("private void ClearPythonConsole()", source);
    Assert.Contains("consoleLines.Clear();", source);
    Assert.Contains("ConsoleTextBox.Text = string.Empty;", source);
    Assert.Contains("settings.AsrEngine", snippet);
    Assert.Contains("settings.SttModel", snippet);
    Assert.Contains("settings.DiarizationEnabled", snippet);
    Assert.Contains("settings.DiarizationModel", snippet);
}

static void WorkerClientWaitsForListeningBeforeReturningStart()
{
    var source = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "Services", "WorkerClient.cs"));

    Assert.Contains("pendingStart", source);
    Assert.Contains("await WaitForWorkerListeningAsync(token);", source);
    Assert.Contains("private Task WaitForWorkerListeningAsync", source);
    Assert.Contains("CompletePendingStart", source);
    Assert.Contains("status.Stage.Equals(\"listening\"", source);
    Assert.Contains("status.Stage.Equals(\"setup_failed\"", source);
}

static void MainWindowRemovesDefaultWhiteResizeBorder()
{
    var xaml = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml"));

    Assert.Contains("xmlns:shell=\"clr-namespace:System.Windows.Shell;assembly=PresentationFramework\"", xaml);
    Assert.Contains("<shell:WindowChrome.WindowChrome>", xaml);
    Assert.Contains("GlassFrameThickness=\"0\"", xaml);
    Assert.Contains("CaptionHeight=\"0\"", xaml);
    Assert.Contains("ResizeBorderThickness=\"6\"", xaml);
}

static void MainWindowUsesRoundedOuterCorners()
{
    var xaml = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml"));
    var source = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml.cs"));

    Assert.Contains("CornerRadius=\"10\"", xaml);
    Assert.Contains("SourceInitialized += MainWindow_SourceInitialized;", source);
    Assert.Contains("private void MainWindow_SourceInitialized(object? sender, EventArgs e)", source);
    Assert.Contains("DwmSetWindowAttribute", source);
    Assert.Contains("DWMWA_WINDOW_CORNER_PREFERENCE", source);
    Assert.Contains("DWMWCP_ROUND", source);
}

static void MainWindowHasConsolePageAndInlineDebugState()
{
    var xaml = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml"));
    var source = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml.cs"));

    Assert.Contains("x:Name=\"ConsoleNavButton\"", xaml);
    Assert.Contains("x:Name=\"ConsolePanel\"", xaml);
    Assert.Contains("x:Name=\"ConsoleTextBox\"", xaml);
    Assert.Contains("x:Name=\"DebugStateText\"", xaml);
    Assert.Contains("DebugButton_Click", xaml);
    Assert.Contains("UpdateDebugStateText();", source);
    Assert.Contains("loadedSttModel", source);
    Assert.Contains("sttLoaded", source);
    Assert.Contains("diarizationLoaded", source);
}

static void MainWindowRemovesHistoryPage()
{
    var xaml = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml"));
    var source = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml.cs"));

    Assert.True(!xaml.Contains("HistoryNavButton", StringComparison.Ordinal), "History page navigation should be removed.");
    Assert.True(!source.Contains("HistoryNavButton", StringComparison.Ordinal), "History page click handling should be removed.");
    Assert.True(!source.Contains("AppPage.History", StringComparison.Ordinal), "History should not be an app page.");
    Assert.True(!source.Contains("CaptionHistoryStore", StringComparison.Ordinal), "Main window should not persist captions for a removed history page.");
    Assert.True(!source.Contains("historyStore", StringComparison.Ordinal), "Main window should not keep unused history storage state.");
    Assert.True(!source.Contains("LoadRecentHistory", StringComparison.Ordinal), "History reload path should be removed.");
    Assert.True(!source.Contains("ShowHistoryDetailMode", StringComparison.Ordinal), "History detail mode should be removed.");
    Assert.Contains("CaptionPanel.Visibility = page == AppPage.Captions ? Visibility.Visible : Visibility.Collapsed;", source);
}

static void TranslationServiceUsesGoogleProviderAndDummyProviders()
{
    var source = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "Services", "TranslationService.cs"));
    var settings = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "Models", "AppSettings.cs"));

    Assert.Contains("public enum TranslateProvider", settings);
    Assert.Contains("Google2", settings);
    Assert.Contains("LibreTranslate", settings);
    Assert.Contains("TranslateProvider provider", source);
    Assert.Contains("translate.googleapis.com/translate_a/single", source);
    Assert.Contains("TranslateProvider.Google =>", source);
    Assert.Contains("DummyTranslateAsync(provider", source);
}

static void MainWindowExposesTranslationProviderAndDisplayModes()
{
    var xaml = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml"));
    var source = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml.cs"));
    var settings = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "Models", "AppSettings.cs"));
    var localizerSource = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "Services", "Localizer.cs"));

    Assert.Contains("x:Name=\"TranslateProviderBox\"", xaml);
    Assert.Contains("x:Name=\"TranslateApiSettingsButton\"", xaml);
    Assert.Contains("x:Name=\"TargetLanguageBox\"", xaml);
    Assert.Contains("x:Name=\"DisplayOriginalRadio\"", xaml);
    Assert.Contains("x:Name=\"DisplayTranslatedRadio\"", xaml);
    Assert.Contains("x:Name=\"DisplayBothRadio\"", xaml);
    Assert.Contains("settings.TranslateProvider = ParseSelectedTag(TranslateProviderBox, settings.TranslateProvider);", source);
    Assert.Contains("settings.TargetLanguage = ParseSelectedTag(TargetLanguageBox, settings.TargetLanguage);", source);
    Assert.Contains("settings.CaptionDisplayMode = SelectedCaptionDisplayMode();", source);
    Assert.Contains("public TranslateProvider TranslateProvider", settings);
    Assert.Contains("public CaptionDisplayMode CaptionDisplayMode", settings);
    Assert.Contains("TranslateApi", localizerSource);
    Assert.Contains("TargetLanguage", localizerSource);
    Assert.Contains("DisplayBoth", localizerSource);
}

static void MainWindowAppliesTargetTranslationLanguageImmediately()
{
    var xaml = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml"));
    var source = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml.cs"));

    Assert.Contains("x:Name=\"TranslateProviderBox\" Height=\"28\" SelectionChanged=\"TranslationSetting_SelectionChanged\"", xaml);
    Assert.Contains("x:Name=\"TargetLanguageBox\" Height=\"28\" MaxDropDownHeight=\"220\" SelectionChanged=\"TranslationSetting_SelectionChanged\"", xaml);
    Assert.Contains("private void TranslationSetting_SelectionChanged", source);
    Assert.Contains("ApplyTranslationSettingsImmediately();", source);
    Assert.Contains("private void ApplyTranslationSettingsImmediately()", source);
    Assert.Contains("SaveSettingsFromUi();", source);
    Assert.Contains("ApplyDisplaySettings();", source);
    Assert.Contains("ClearTranslations();", source);
    Assert.Contains("StartTranslationForEntryAsync(entry);", source);
}

static void MainWindowRendersTranslatedCaptionsBelowOriginals()
{
    var xaml = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml"));
    var source = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml.cs"));
    var viewModel = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "ViewModels", "CaptionEntryViewModel.cs"));
    var overlayViewModel = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "ViewModels", "OverlaySpeakerViewModel.cs"));

    Assert.Contains("x:Name=\"CurrentOriginalText\"", xaml);
    Assert.Contains("x:Name=\"CurrentTranslationText\"", xaml);
    Assert.Contains("StartTranslationForEntryAsync(entry);", source);
    Assert.Contains("translationTexts[entry.Id]", source);
    Assert.Contains("UpdateCurrentCaption(entry);", source);
    Assert.Contains("new CaptionEntryViewModel(entry, TranslationFor(entry), settings.CaptionDisplayMode)", source);
    Assert.Contains("public string OriginalText", viewModel);
    Assert.Contains("public string TranslatedText", viewModel);
    Assert.Contains("public string DisplayOriginalText", viewModel);
    Assert.Contains("public string DisplayTranslatedText", viewModel);
    Assert.Contains("public string DisplayText", viewModel);
    Assert.Contains("OriginalDisplayText", overlayViewModel);
    Assert.Contains("TranslatedDisplayText", overlayViewModel);
}

static void MainWindowThrottlesGoogleTranslationRequests()
{
    var source = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml.cs"));

    Assert.Contains("private static readonly TimeSpan TranslationDebounceDelay", source);
    Assert.Contains("private readonly SemaphoreSlim translationGate = new(1, 1);", source);
    Assert.Contains("if (!entry.IsFinal)", source);
    Assert.Contains("await Task.Delay(TranslationDebounceDelay, cts.Token);", source);
    Assert.Contains("await translationGate.WaitAsync(cts.Token);", source);
    Assert.Contains("translationGate.Release();", source);
    Assert.Contains("new WorkerLogLine(\"translation\"", source);
    Assert.True(!source.Contains("previous.Dispose();", StringComparison.Ordinal), "canceled translation tasks must dispose their own token source in finally.");
}

static void CaptionDisplaySuppressesDuplicateTranslations()
{
    var source = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml.cs"));
    var viewModel = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "ViewModels", "CaptionEntryViewModel.cs"));

    Assert.Contains("BuildDisplayTranslatedText(OriginalText, TranslatedText, displayMode)", viewModel);
    Assert.Contains("IsDuplicateTranslation(original, translated)", viewModel);
    Assert.Contains("NormalizeComparableText", viewModel);
    Assert.Contains("return \"\";", viewModel);
    Assert.Contains("HasUsefulTranslation(original, translated)", source);
    Assert.Contains("!IsDuplicateTranslation(original, translated)", source);
    Assert.Contains("CurrentTranslationText.Text = hasTranslation ? translated : \"\";", source);
}

static void MainWindowDoesNotSwitchPagesForBackgroundWorkerUpdates()
{
    var source = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml.cs"));

    Assert.Contains("private AppPage activePage = AppPage.Captions;", source);
    Assert.Contains("if (activePage == AppPage.Captions)", source);
    Assert.Contains("private enum AppPage", source);
}

static void MainWindowExposesLargeWhisperModelOptions()
{
    var xaml = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml"));

    Assert.Contains("<ComboBoxItem x:Name=\"SttLargeV3Item\" Content=\"large-v3\" />", xaml);
    Assert.Contains("<ComboBoxItem x:Name=\"SttLargeV3TurboItem\" Content=\"large-v3-turbo\" />", xaml);
}

static void MainWindowRemovesLiveCaptionsAndEngineSelection()
{
    var xaml = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml"));
    var source = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml.cs"));

    Assert.True(!xaml.Contains("SttEngineBox", StringComparison.Ordinal), "STT engine selection must be removed because only Local Whisper is supported.");
    Assert.True(!xaml.Contains("WindowsLiveCaptions", StringComparison.Ordinal), "Windows Live Captions must not be offered as an STT engine.");
    Assert.True(!xaml.Contains("LiveCaptionsButton", StringComparison.Ordinal), "Live Captions show/hide controls must be removed.");
    Assert.True(!source.Contains("LiveCaptionsReaderService", StringComparison.Ordinal), "MainWindow must not instantiate the Windows Live Captions reader.");
    Assert.True(!source.Contains("SttEngineBox_SelectionChanged", StringComparison.Ordinal), "STT engine switching code must be removed.");
    Assert.True(!source.Contains("LiveCaptionsButton_Click", StringComparison.Ordinal), "Live Captions button handler must be removed.");
}

static void MainWindowExposesWhisperSttLanguageSelection()
{
    var xaml = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml"));
    var source = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml.cs"));
    var settings = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "Models", "AppSettings.cs"));
    var protocol = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.Core", "Protocol", "WorkerProtocol.cs"));
    var languageWindow = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "SttLanguageWindow.xaml.cs"));

    Assert.Contains("x:Name=\"SttLanguagesButton\"", xaml);
    Assert.Contains("Click=\"SttLanguagesButton_Click\"", xaml);
    Assert.Contains("new SttLanguageWindow(settings.SttLanguages, localizer)", source);
    Assert.Contains("settings.SttLanguages = window.SelectedLanguages;", source);
    Assert.True(!source.Contains("SttLanguagesButton.IsEnabled = localWhisper;", StringComparison.Ordinal), "Whisper language selection should stay enabled because there is no non-Whisper engine.");
    Assert.Contains("public List<string> SttLanguages", settings);
    Assert.Contains("[\"sttLanguages\"] = configuration.SttLanguages", protocol);
    Assert.Contains("(\"ko\", \"Korean\", \"한국어\")", languageWindow);
}

static void MainWindowSeparatesAsrEngineAndModelChoices()
{
    var xaml = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml"));
    var source = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml.cs"));
    var localizerSource = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "Services", "Localizer.cs"));

    Assert.Contains("x:Name=\"AsrEngineLabel\"", xaml);
    Assert.Contains("x:Name=\"AsrEngineFasterWhisperItem\"", xaml);
    Assert.Contains("x:Name=\"AsrEngineHintText\"", xaml);
    Assert.Contains("AsrEngine", localizerSource);
    Assert.Contains("FasterWhisper", localizerSource);
    Assert.Contains("QwenAsr", localizerSource);
    Assert.Contains("WhisperLiveKit", localizerSource);
    Assert.Contains("WhisperX", localizerSource);
    Assert.Contains("[\"AsrEngineHint\"]", localizerSource);
    Assert.Contains("AsrModel", localizerSource);
    Assert.Contains("SyncSttModelItemsForEngine", source);
    Assert.Contains("SttDefaultItem", xaml);
    Assert.Contains("DefaultWhisperLiveKitModel", source);
    Assert.Contains("var whisperX = engine == AsrEngine.WhisperX;", source);
    Assert.Contains("SttTinyItem.Visibility = fasterWhisper || whisperX ? Visibility.Visible : Visibility.Collapsed;", source);
    Assert.Contains("SttQwen06BItem.Visibility = qwenAsr ? Visibility.Visible : Visibility.Collapsed;", source);
    Assert.Contains("SttDefaultItem.Visibility = whisperLiveKit ? Visibility.Visible : Visibility.Collapsed;", source);
}

static void MainWindowExposesSpeakerCountRadios()
{
    var xaml = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml"));
    var source = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml.cs"));

    Assert.Contains("x:Name=\"SpeakerCountPanel\"", xaml);
    Assert.Contains("Columns=\"4\"", xaml);
    for (var count = 1; count <= 8; count++)
    {
        Assert.Contains($"Tag=\"auto:{count}\"", xaml);
    }

    Assert.True(!xaml.Contains("x:Name=\"MaxSpeakersBox\"", StringComparison.Ordinal), "speaker count should be radio buttons, not a dropdown.");
    Assert.Contains("SelectedSpeakerCountTag()", source);
    Assert.Contains("SelectSpeakerCount(settings)", source);
}

static void MainWindowExposesSpeakerCountModeOptions()
{
    var xaml = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml"));
    var source = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml.cs"));
    var localizer = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "Services", "Localizer.cs"));

    Assert.Contains("x:Name=\"SpeakerCountModePanel\"", xaml);
    Assert.Contains("x:Name=\"SpeakerModeActiveMaxRadio\"", xaml);
    Assert.Contains("x:Name=\"SpeakerModeExactRadio\"", xaml);
    Assert.Contains("GroupName=\"SpeakerCountMode\"", xaml);
    Assert.Contains("SelectedSpeakerCountMode()", source);
    Assert.Contains("SelectSpeakerCountMode(settings.SpeakerCountMode);", source);
    Assert.Contains("settings.SpeakerCountMode = SelectedSpeakerCountMode();", source);
    Assert.Contains("settings.ExactSpeakers = settings.SpeakerCountMode == SpeakerCountMode.Exact ? settings.MaxSpeakers : null;", source);
    Assert.Contains("SpeakerModeActiveMax", localizer);
    Assert.Contains("SpeakerModeExact", localizer);
    Assert.Contains("SpeakerModeHelp", localizer);
}

static void MainWindowDeselectsDiarizationForOneSpeaker()
{
    var source = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml.cs"));

    Assert.Contains("NormalizeDiarizationForSpeakerCount();", source);
    Assert.Contains("private void NormalizeDiarizationForSpeakerCount()", source);
    Assert.Contains("SelectedSpeakerCountMax() == 1", source);
    Assert.Contains("DiarizationCheck.IsChecked = false;", source);
    Assert.Contains("private int SelectedSpeakerCountMax()", source);
    Assert.True(
        source.IndexOf("NormalizeDiarizationForSpeakerCount();", StringComparison.Ordinal) <
        source.IndexOf("settings.DiarizationEnabled = DiarizationCheck.IsChecked == true;", StringComparison.Ordinal),
        "speaker count normalization must run before saving the diarization flag.");
}

static void MainWindowSeparatesAsrAndDiarizationPresets()
{
    var xaml = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml"));
    var source = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml.cs"));
    var settings = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "Models", "AppSettings.cs"));
    var protocol = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.Core", "Protocol", "WorkerProtocol.cs"));

    Assert.Contains("x:Name=\"SttPresetLabel\"", xaml);
    Assert.Contains("x:Name=\"DiarizationPresetLabel\"", xaml);
    Assert.Contains("x:Name=\"DiarizationPresetSensitiveRadio\"", xaml);
    Assert.Contains("x:Name=\"DiarizationPresetBalancedRadio\"", xaml);
    Assert.Contains("x:Name=\"DiarizationPresetStableRadio\"", xaml);
    Assert.Contains("public int DiarizationQualityPreset", settings);
    Assert.Contains("[\"diarizationQualityPreset\"] = configuration.DiarizationQualityPreset", protocol);
    Assert.Contains("settings.DiarizationQualityPreset = SelectedDiarizationQualityPreset();", source);
}

static void MainWindowExposesSttScenarioPresetRadios()
{
    var xaml = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml"));
    var source = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml.cs"));
    var settings = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "Models", "AppSettings.cs"));
    var protocol = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.Core", "Protocol", "WorkerProtocol.cs"));
    var localizerSource = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "Services", "Localizer.cs"));

    Assert.Contains("x:Name=\"SttPresetSpeedRadio\"", xaml);
    Assert.Contains("x:Name=\"AsrEngineBox\"", xaml);
    Assert.Contains("Tag=\"Qwen3Asr\"", xaml);
    Assert.Contains("Tag=\"WhisperLiveKitSortformer\"", xaml);
    Assert.Contains("Tag=\"WhisperX\"", xaml);
    Assert.Contains("x:Name=\"SttPresetDebateRadio\"", xaml);
    Assert.Contains("x:Name=\"SttPresetTalkShowRadio\"", xaml);
    Assert.Contains("GroupName=\"SttPreset\"", xaml);
    Assert.Contains("Checked=\"RestartSetting_CheckedChanged\"", xaml);
    Assert.True(!xaml.Contains("x:Name=\"SttPresetSlider\"", StringComparison.Ordinal), "preset must be a two-choice radio group, not a free-form slider.");
    Assert.Contains("SttPresetSummaryText", xaml);
    Assert.Contains("MinHeight=\"16\"", xaml);
    Assert.True(!xaml.Contains("<RowDefinition Height=\"38\" />", StringComparison.Ordinal), "Preset summary row must not be clipped by a fixed-height settings row.");
    Assert.Contains("settings.SttQualityPreset = SelectedSttQualityPreset();", source);
    Assert.Contains("settings.AsrEngine = ParseSelectedTag(AsrEngineBox, settings.AsrEngine);", source);
    Assert.Contains("SelectSttPreset(settings.SttQualityPreset);", source);
    Assert.Contains("SelectByTag(AsrEngineBox, settings.AsrEngine.ToString());", source);
    Assert.Contains("private int SelectedSttQualityPreset()", source);
    Assert.Contains("return SttPresetDebateRadio.IsChecked == true ? 50 : 0;", source);
    Assert.Contains("private string SttPresetName(int quality)", source);
    Assert.Contains("UpdateSttPresetSummary();", source);
    Assert.Contains("SttChunkSecondsForPreset", source);
    Assert.Contains("DiartLatencySecondsForPreset", source);
    Assert.Contains("public int SttQualityPreset", settings);
    Assert.Contains("[\"sttQualityPreset\"] = configuration.SttQualityPreset", protocol);
    Assert.Contains("SttPreset", localizerSource);
    Assert.Contains("Sensitive", localizerSource);
    Assert.Contains("Balanced", localizerSource);
    Assert.Contains("Stable", localizerSource);
    Assert.Contains("SttPresetSummaryCommunity", localizerSource);
    Assert.Contains("SttPresetSummaryCommunityStable", localizerSource);
    Assert.Contains("SttPresetSummaryCommunityStable", source);
    Assert.Contains("SttPresetSummaryDiart", localizerSource);
    Assert.Contains("QwenAsr", localizerSource);
    Assert.Contains("WhisperLiveKit", localizerSource);
    Assert.Contains("WhisperX", localizerSource);
    Assert.True(!localizerSource.Contains("\uC2E4\uD5D8", StringComparison.Ordinal), "ASR engine labels must not use trial wording.");
}

static void MainWindowExposesSpeakerRangeCountOptions()
{
    var xaml = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml"));
    var source = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml.cs"));
    var store = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "Services", "SettingsStore.cs"));

    Assert.Contains("Tag=\"auto:1\"", xaml);
    Assert.Contains("Tag=\"auto:8\"", xaml);
    Assert.True(!xaml.Contains("Tag=\"exact:", StringComparison.Ordinal), "speaker count UI must use max-range options, not exact speaker forcing");
    Assert.Contains("SelectedSpeakerCountTag()", source);
    Assert.Contains("settings.ExactSpeakers = null;", store);
    Assert.Contains("exactSpeakers,", store);
}

static void MainWindowExposesDiarizationModelOptions()
{
    var xaml = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml"));
    var source = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml.cs"));
    var settings = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "Models", "AppSettings.cs"));
    var store = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "Services", "SettingsStore.cs"));
    var protocol = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.Core", "Protocol", "WorkerProtocol.cs"));
    var environment = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "Services", "WorkerEnvironmentService.cs"));

    Assert.Contains("x:Name=\"DiarizationCommunityRadio\"", xaml);
    Assert.Contains("x:Name=\"DiarizationDiartRadio\"", xaml);
    Assert.Contains("x:Name=\"DiarizationSortformerRadio\"", xaml);
    Assert.True(!xaml.Contains("x:Name=\"DiarizationNemoSortformerRadio\"", StringComparison.Ordinal), "NeMo diarization must not be exposed.");
    Assert.Contains("GroupName=\"DiarizationModel\"", xaml);
    Assert.True(!xaml.Contains("x:Name=\"DiarizationModelBox\"", StringComparison.Ordinal), "diarization model selection must be a visible radio group, not a dropdown.");
    Assert.Contains("settings.DiarizationModel = SelectedDiarizationModel();", source);
    Assert.Contains("SelectDiarizationModel(settings.DiarizationModel);", source);
    Assert.Contains("private DiarizationModel SelectedDiarizationModel()", source);
    Assert.Contains("public DiarizationModel DiarizationModel", settings);
    Assert.Contains("Sortformer", protocol);
    Assert.True(!protocol.Contains("NemoSortformer", StringComparison.Ordinal), "NeMo protocol support must be removed.");
    Assert.Contains("settings.DiarizationModel", store);
    Assert.Contains("NormalizeLegacyRemovedSpeechModels", store);
    Assert.Contains("[\"diarizationModel\"]", protocol);
    Assert.Contains("LIVE_DIALOGUE_TRANSLATOR_DIARIZATION_MODEL", environment);
    Assert.Contains("InstallOptionalDiarizationPackagesAsync(settings", environment);
}

static void MainWindowAllowsAsrAndDiarizationCombinations()
{
    var xaml = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml"));
    var source = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml.cs"));
    var localizerSource = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "Services", "Localizer.cs"));

    Assert.Contains("x:Name=\"SttQwen06BItem\"", xaml);
    Assert.Contains("x:Name=\"SttQwen17BItem\"", xaml);
    Assert.Contains("x:Name=\"SttLargeV3TurboItem\"", xaml);
    Assert.Contains("x:Name=\"DiarizationModelPanel\"", xaml);
    Assert.Contains("ApplyAsrEngineUiState(normalizeSelection: true);", source);
    Assert.Contains("DiarizationModelPanel.Visibility = Visibility.Visible;", source);
    Assert.True(!source.Contains("DiarizationCheck.IsEnabled = !isWhisperLiveKit;", StringComparison.Ordinal), "ASR engine must not lock the diarization toggle.");
    Assert.True(!source.Contains("DiarizationModelPanel.Visibility = isWhisperLiveKit ? Visibility.Collapsed : Visibility.Visible;", StringComparison.Ordinal), "WhisperLiveKit must not hide diarization model choices.");
    Assert.Contains("Sortformer", localizerSource);
    Assert.True(!localizerSource.Contains("NemoSortformer", StringComparison.Ordinal), "NeMo localization must be removed.");
}

static void MainWindowExposesManualDiartTuningControls()
{
    var xaml = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml"));
    var source = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml.cs"));
    var settings = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "Models", "AppSettings.cs"));
    var store = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "Services", "SettingsStore.cs"));
    var protocol = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.Core", "Protocol", "WorkerProtocol.cs"));
    var localizerSource = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "Services", "Localizer.cs"));
    var worker = File.ReadAllText(Path.Combine("worker", "speaker_worker.py"));

    Assert.Contains("x:Name=\"DiartManualCheck\"", xaml);
    Assert.Contains("x:Name=\"DiartManualPanel\"", xaml);
    Assert.Contains("x:Name=\"DiartDurationBox\"", xaml);
    Assert.Contains("x:Name=\"DiartStepBox\"", xaml);
    Assert.Contains("x:Name=\"DiartLatencyBox\"", xaml);
    Assert.Contains("x:Name=\"DiartTauBox\"", xaml);
    Assert.Contains("x:Name=\"DiartRhoBox\"", xaml);
    Assert.Contains("x:Name=\"DiartDeltaBox\"", xaml);
    Assert.Contains("TextChanged=\"RestartSetting_TextChanged\"", xaml);
    Assert.Contains("ParseDoubleText(DiartDurationBox", source);
    Assert.Contains("DiartManualPanel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;", source);
    Assert.Contains("public bool DiartManualSettings", settings);
    Assert.Contains("public double DiartDurationSeconds", settings);
    Assert.Contains("settings.DiartDurationSeconds", store);
    Assert.Contains("\"diartManualSettings\"", protocol);
    Assert.Contains("\"diartDurationSeconds\"", protocol);
    Assert.Contains("DiartManualDescription", localizerSource);
    Assert.Contains("SttPresetSummaryDiartManual", localizerSource);
    Assert.Contains("diart_manual_settings", worker);
    Assert.Contains("diart_duration_seconds", worker);
    Assert.Contains("SpeakerDiarizationConfig(", worker);
    Assert.Contains("duration=duration", worker);
    Assert.Contains("**hyper_parameters", worker);
}

static void ModelManagerExposesSeparateHfModelTermButtons()
{
    var xaml = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "ModelManagerWindow.xaml"));
    var source = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "ModelManagerWindow.xaml.cs"));
    var links = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.Core", "Startup", "HuggingFaceLinks.cs"));

    Assert.Contains("CommunityTermsButton", xaml);
    Assert.Contains("DiartSegmentationTermsButton", xaml);
    Assert.Contains("DiartEmbeddingTermsButton", xaml);
    Assert.Contains("OpenCommunityTerms_Click", source);
    Assert.Contains("OpenDiartSegmentationTerms_Click", source);
    Assert.Contains("OpenDiartEmbeddingTerms_Click", source);
    Assert.Contains("UpdateModelTermsButtons()", source);
    Assert.Contains("DiartSegmentationModelUrl", links);
    Assert.Contains("DiartEmbeddingModelUrl", links);
}

static void MainWindowAppliesSettingsChangesImmediately()
{
    var xaml = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml"));
    var source = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml.cs"));

    Assert.Contains("SelectionChanged=\"RestartSetting_SelectionChanged\"", xaml);
    Assert.Contains("Checked=\"RestartSetting_CheckedChanged\"", xaml);
    Assert.Contains("SelectionChanged=\"ImmediateSetting_SelectionChanged\"", xaml);
    Assert.Contains("Checked=\"ImmediateSetting_CheckedChanged\"", xaml);
    Assert.Contains("QueueSettingsApplyAsync(restartIfRunning: true)", source);
    Assert.Contains("QueueSettingsApplyAsync(restartIfRunning: false)", source);
    Assert.Contains("await StopCaptureAsync(showStopped: false);", source);
    Assert.Contains("await StartCaptureAsync(showCaptionsPage: false);", source);
    Assert.Contains("ApplyDisplaySettings();", source);
    Assert.Contains("suppressSettingsChange", source);
}

static void MainWindowFitsSettingsPageHeightToContent()
{
    var xaml = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml"));
    var source = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml.cs"));
    var localizerSource = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "Services", "Localizer.cs"));

    Assert.Contains("x:Name=\"SettingsPanel\" Margin=\"10,8,10,6\" VerticalAlignment=\"Top\"", xaml);
    Assert.Contains("<Grid x:Name=\"SettingsPanel\"", xaml);
    Assert.True(!xaml.Contains("<Setter Property=\"Width\" Value=\"258\" />", StringComparison.Ordinal), "Settings groups should not use the old wide fixed card width.");
    Assert.Contains("<ColumnDefinition Width=\"1*\" />", xaml);
    Assert.Contains("SettingsGroupBorderStyle", xaml);
    Assert.Contains("<Style x:Key=\"SettingsGroupTitleStyle\"", xaml);
    Assert.Contains("<Setter Property=\"Foreground\" Value=\"{StaticResource MutedBrush}\" />", xaml);
    Assert.Contains("SettingsAudioGroupTitle", xaml);
    Assert.Contains("SettingsModelGroupTitle", xaml);
    Assert.Contains("SettingsDiarizationGroupTitle", xaml);
    Assert.Contains("SettingsTranslationGroupTitle", xaml);
    Assert.Contains("SettingsOverlayGroupTitle", xaml);
    Assert.Contains("SettingsToolsGroupTitle", xaml);
    Assert.Contains("SettingsAudioGroupTitle.Text = L(\"SettingsAudioGroup\")", source);
    Assert.Contains("[\"SettingsAudioGroup\"]", localizerSource);
    Assert.Contains("[\"SettingsModelGroup\"]", localizerSource);
    Assert.Contains("[\"SettingsTranslationGroup\"]", localizerSource);
    Assert.Contains("[\"SettingsOverlayGroup\"]", localizerSource);
    Assert.Contains("AdjustWindowHeightToSettingsContent();", source);
    Assert.Contains("SettingsPanel.Measure(new Size", source);
    Assert.Contains("double.PositiveInfinity", source);
    Assert.Contains("SettingsPanel.DesiredSize.Height", source);
    Assert.Contains("SystemParameters.WorkArea.Height", source);
}

static void MainWindowPutsDisplayLinesInOverlaySettings()
{
    var xaml = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml"));

    var diarizationGroup = xaml.IndexOf("x:Name=\"SettingsDiarizationGroupTitle\"", StringComparison.Ordinal);
    var translationGroup = xaml.IndexOf("x:Name=\"SettingsTranslationGroupTitle\"", StringComparison.Ordinal);
    var overlayGroup = xaml.IndexOf("x:Name=\"SettingsOverlayGroupTitle\"", StringComparison.Ordinal);
    var displayLines = xaml.IndexOf("x:Name=\"OverlayDisplayLinesLabel\"", StringComparison.Ordinal);

    Assert.True(diarizationGroup >= 0 && translationGroup > diarizationGroup && overlayGroup > translationGroup && displayLines > overlayGroup,
        "Lines per speaker should live in the Overlay category, not the diarization category.");
    Assert.Contains("x:Name=\"OverlayDisplayLinesBox\"", xaml);
}

static void MainWindowSeparatesCaptionAndOverlayLineLimits()
{
    var settingsSource = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "Models", "AppSettings.cs"));
    var mainXaml = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml"));
    var mainSource = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml.cs"));
    var localizerSource = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "Services", "Localizer.cs"));

    Assert.Contains("public int CaptionDisplayLines", settingsSource);
    Assert.Contains("public int OverlayDisplayLines", settingsSource);
    Assert.Contains("x:Name=\"CaptionDisplayLinesLabel\"", mainXaml);
    Assert.Contains("x:Name=\"CaptionDisplayLinesBox\"", mainXaml);
    Assert.Contains("x:Name=\"OverlayDisplayLinesLabel\"", mainXaml);
    Assert.Contains("x:Name=\"OverlayDisplayLinesBox\"", mainXaml);
    Assert.Contains("CaptionDisplayLinesLabel.Text = L(\"CaptionDisplayLines\")", mainSource);
    Assert.Contains("OverlayDisplayLinesLabel.Text = L(\"OverlayDisplayLines\")", mainSource);
    Assert.Contains("settings.CaptionDisplayLines", mainSource);
    Assert.Contains("settings.OverlayDisplayLines", mainSource);
    Assert.Contains("new CaptionMerger(MaxRetainedDisplayLines(), speakerNames)", mainSource);
    Assert.Contains("CaptionDisplayLines", localizerSource);
    Assert.Contains("OverlayDisplayLines", localizerSource);
}

static void WorkerProtocolNoLongerSerializesSttEngineSelection()
{
    var source = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.Core", "Protocol", "WorkerProtocol.cs"));

    Assert.True(!source.Contains("public enum SttEngine", StringComparison.Ordinal), "STT engine enum should be removed with Live Captions.");
    Assert.True(!source.Contains("[\"sttEngine\"]", StringComparison.Ordinal), "Worker configure payload should not serialize a removed engine switch.");
    Assert.True(!source.Contains("FormatSttEngine", StringComparison.Ordinal), "STT engine formatter should be removed.");
    Assert.True(!source.Contains("windows_live_captions", StringComparison.Ordinal), "Worker protocol must not know about Windows Live Captions.");
}

static void WorkerEnvironmentDoesNotExportSttEngineToSetupChecks()
{
    var source = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "Services", "WorkerEnvironmentService.cs"));

    Assert.True(!source.Contains("LIVE_DIALOGUE_TRANSLATOR_STT_ENGINE", StringComparison.Ordinal), "Setup checks should always prepare local Whisper and should not export a removed engine switch.");
    Assert.True(!source.Contains("settings.SttEngine", StringComparison.Ordinal), "Worker environment should not branch on STT engine.");
}

static void SettingsModelHasNoSttEngine()
{
    var settings = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "Models", "AppSettings.cs"));
    var store = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "Services", "SettingsStore.cs"));

    Assert.True(!settings.Contains("SttEngine", StringComparison.Ordinal), "Persisted app settings should not keep a removed STT engine option.");
    Assert.True(!store.Contains("settings.SttEngine", StringComparison.Ordinal), "Worker configuration should always use local Whisper without a stored engine value.");
}

static void AsrEngineEnvironmentPrioritizesSortformerDependencySite()
{
    var source = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "Services", "AsrEngineEnvironment.cs"));

    Assert.Contains("OrderAsrPackageEngines", source);
    Assert.Contains("AsrEngine.WhisperLiveKitSortformer ? 0 : 1", source);
    Assert.Contains(".Select(paths.AsrPackageDirectory)", source);
}

static void OverlayGroupsCaptionsBySpeakerAndFadesInactiveSpeakers()
{
    var overlayXaml = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "OverlayWindow.xaml"));
    var overlaySource = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "OverlayWindow.xaml.cs"));
    var overlayViewModel = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "ViewModels", "OverlaySpeakerViewModel.cs"));
    var mainSource = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml.cs"));
    var captionViewModel = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "ViewModels", "CaptionEntryViewModel.cs"));

    Assert.Contains("x:Name=\"OverlaySpeakers\"", overlayXaml);
    Assert.True(!overlayXaml.Contains("ItemsSource=\"{Binding Lines}\"", StringComparison.Ordinal), "overlay must render one grouped original block and one grouped translation block per speaker instead of alternating original/translation per caption line.");
    Assert.Contains("local:SegmentedTextBlockBehavior.Segments=\"{Binding OriginalSegments}\"", overlayXaml);
    Assert.Contains("local:SegmentedTextBlockBehavior.Segments=\"{Binding TranslationSegments}\"", overlayXaml);
    Assert.Contains("IsFading", overlayXaml);
    Assert.Contains("UpdateEntry(CaptionEntryViewModel entry, int linesPerSpeaker)", overlaySource);
    Assert.Contains("InactiveTimeout", overlaySource);
    Assert.Contains("InsertSpeakerSorted", overlaySource);
    Assert.Contains("clearedSpeakerEndMs", overlaySource);
    Assert.Contains("IsBeforeClearedSpeakerBoundary(entry)", overlaySource);
    Assert.Contains("entry.EndMs <= clearedEndMs", overlaySource);
    Assert.Contains("RecordClearedSpeakerBoundary(speaker);", overlaySource);
    Assert.Contains("speaker.ClearLinesAfterFade();", overlaySource);
    Assert.Contains("ClearSessionEntries()", overlaySource);
    Assert.Contains("while (Lines.Count > LinesPerSpeaker)", overlayViewModel);
    Assert.Contains("RefreshDisplaySegments();", overlayViewModel);
    Assert.Contains("AppendSegments(OriginalSegments", overlayViewModel);
    Assert.Contains("AppendSegments(TranslationSegments", overlayViewModel);
    Assert.Contains("LatestLineEndMs", overlayViewModel);
    Assert.Contains("ClearLinesAfterFade()", overlayViewModel);
    Assert.Contains("Lines.Clear();", overlayViewModel);
    Assert.Contains("OriginalDisplayText = string.Empty;", overlayViewModel);
    Assert.Contains("TranslatedDisplayText = string.Empty;", overlayViewModel);
    Assert.Contains("overlayWindow?.ClearSessionEntries();", mainSource);
    Assert.Contains("public Guid Id", captionViewModel);
}

static void OverlayCapsVisibleTextRowsPerSpeaker()
{
    var overlayXaml = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "OverlayWindow.xaml"));
    var overlayViewModel = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "ViewModels", "OverlaySpeakerViewModel.cs"));

    Assert.True(!overlayXaml.Contains("ItemsSource=\"{Binding Lines}\"", StringComparison.Ordinal), "overlay line cap should be applied to grouped original/translation blocks, not repeated line pairs.");
    Assert.Contains("ClipToBounds=\"True\"", overlayXaml);
    Assert.Contains("LineLimitHeightConverter", overlayXaml);
    Assert.Contains("<TextBlock.MaxHeight>", overlayXaml);
    Assert.Contains("Path=\"LinesPerSpeaker\"", overlayXaml);
    Assert.Contains("local:SegmentedTextBlockBehavior.Segments=\"{Binding OriginalSegments}\"", overlayXaml);
    Assert.Contains("local:SegmentedTextBlockBehavior.Segments=\"{Binding TranslationSegments}\"", overlayXaml);
    Assert.True(!overlayXaml.Contains("<ItemsControl.MaxHeight>", StringComparison.Ordinal), "line cap must apply separately to original and translated text, not to their combined container.");
    Assert.Contains("Margin=\"0,4,0,0\"", overlayXaml);
    Assert.Contains("public string OriginalDisplayText", overlayViewModel);
    Assert.Contains("public string TranslatedDisplayText", overlayViewModel);
    Assert.Contains("public int LinesPerSpeaker", overlayViewModel);
    Assert.Contains("InsertLineSorted(line);", overlayViewModel);
    Assert.Contains("RepositionLine(existing);", overlayViewModel);
    Assert.Contains("\" \",", overlayViewModel);
    Assert.Contains("BuildDisplayText(Lines.Select(line => line.DisplayOriginalText)", overlayViewModel);
    Assert.Contains("BuildDisplayText(Lines.Select(line => line.DisplayTranslatedText)", overlayViewModel);
}

static void CaptionPageCapsVisibleTextRowsPerSpeaker()
{
    var mainXaml = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml"));

    Assert.Contains("x:Name=\"CaptionSpeakerItems\"", mainXaml);
    Assert.Contains("LineLimitHeightConverter", mainXaml);
    Assert.Contains("ClipToBounds=\"True\"", mainXaml);
    Assert.Contains("<TextBlock.MaxHeight>", mainXaml);
    Assert.Contains("Path=\"LinesPerSpeaker\"", mainXaml);
    Assert.Contains("local:SegmentedTextBlockBehavior.Segments=\"{Binding OriginalSegments}\"", mainXaml);
    Assert.Contains("local:SegmentedTextBlockBehavior.Segments=\"{Binding TranslationSegments}\"", mainXaml);
    Assert.True(!mainXaml.Contains("<ItemsControl.MaxHeight>", StringComparison.Ordinal), "line cap must apply separately to original and translated text, not to their combined container.");
}

static void OverlayTrimsOversizedSpeakerTextTailsBeforeTextBlockClipping()
{
    var overlayViewModel = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "ViewModels", "OverlaySpeakerViewModel.cs"));

    Assert.Contains("DisplayCharactersPerLineBudget", overlayViewModel);
    Assert.Contains("SegmentDisplayCharacterLimit", overlayViewModel);
    Assert.Contains("TrimTextToTail(combinedText, Math.Max(1, linesPerSpeaker) * DisplayCharactersPerLineBudget)", overlayViewModel);
    Assert.Contains("AppendSegments(OriginalSegments, Lines.Select(line => line.OriginalSegments), SegmentDisplayCharacterLimit)", overlayViewModel);
    Assert.Contains("AppendSegments(TranslationSegments, Lines.Select(line => line.TranslationSegments), SegmentDisplayCharacterLimit)", overlayViewModel);
    Assert.Contains("TrimSegmentsToTail", overlayViewModel);
}

static void SegmentedTextBlocksTrimTailsByRenderedWidth()
{
    var behavior = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "SegmentedTextBlockBehavior.cs"));
    var overlayXaml = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "OverlayWindow.xaml"));
    var mainXaml = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml"));

    Assert.Contains("MaxVisualLinesProperty", behavior);
    Assert.Contains("GetMaxVisualLines(textBlock)", behavior);
    Assert.Contains("textBlock.SizeChanged += TextBlock_SizeChanged;", behavior);
    Assert.Contains("FormattedText", behavior);
    Assert.Contains("MaxTextWidth = availableWidth", behavior);
    Assert.Contains("TrimSegmentsToVisualLines", behavior);
    Assert.Contains("local:SegmentedTextBlockBehavior.MaxVisualLines=\"{Binding LinesPerSpeaker}\"", overlayXaml);
    Assert.Contains("local:SegmentedTextBlockBehavior.MaxVisualLines=\"{Binding LinesPerSpeaker}\"", mainXaml);
}

static void OverlayRendersCaptionLinesWithoutTrimming()
{
    var overlayXaml = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "OverlayWindow.xaml"));
    var marker = "local:SegmentedTextBlockBehavior.Segments=\"{Binding OriginalSegments}\"";
    var start = overlayXaml.IndexOf(marker, StringComparison.Ordinal);
    Assert.True(start >= 0, "Overlay original display text binding must exist.");
    var textBlockStart = overlayXaml.LastIndexOf("<TextBlock", start, StringComparison.Ordinal);
    var textBlockEnd = overlayXaml.IndexOf("</TextBlock>", start, StringComparison.Ordinal);
    Assert.True(textBlockStart >= 0 && textBlockEnd > start, "Overlay original display text block must be parseable.");
    var snippet = overlayXaml.Substring(textBlockStart, textBlockEnd - textBlockStart);

    Assert.Contains("TextWrapping=\"Wrap\"", snippet);
    Assert.True(!snippet.Contains("TextTrimming=\"CharacterEllipsis\"", StringComparison.Ordinal), "overlay caption text must wrap instead of trimming the end with ellipsis.");
    Assert.True(!snippet.Contains("TextBox", StringComparison.Ordinal), "line-level colors should not be collapsed into a single speaker-level TextBox.");
}

static void OverlayHighlightsCurrentSpeakerText()
{
    var overlayXaml = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "OverlayWindow.xaml"));
    var overlaySource = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "OverlayWindow.xaml.cs"));
    var overlayViewModel = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "ViewModels", "OverlaySpeakerViewModel.cs"));

    Assert.Contains("Foreground=\"{Binding SpeakerBrush}\"", overlayXaml);
    Assert.Contains("MarkActiveSpeaker(speaker);", overlaySource);
    Assert.Contains("public bool IsCurrent", overlayViewModel);
    Assert.Contains("SetCurrent(bool value)", overlayViewModel);
}

static void OverlayHighlightsOnlyNewlyUpdatedCaptionLine()
{
    var overlayXaml = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "OverlayWindow.xaml"));
    var overlayViewModel = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "ViewModels", "OverlaySpeakerViewModel.cs"));

    Assert.True(!overlayXaml.Contains("ItemsSource=\"{Binding Lines}\"", StringComparison.Ordinal), "overlay must not alternate original/translation by rendering each retained caption line separately.");
    Assert.Contains("OverlayCaptionTextSegmentViewModel", overlayViewModel);
    Assert.Contains("public ObservableCollection<OverlayCaptionTextSegmentViewModel> OriginalSegments", overlayViewModel);
    Assert.Contains("public ObservableCollection<OverlayCaptionTextSegmentViewModel> TranslationSegments", overlayViewModel);
    Assert.Contains("SplitChangedText", overlayViewModel);
    Assert.Contains("markIncomingAsCurrentBatch", overlayViewModel);
    Assert.Contains("IsChangedSegment", overlayViewModel);
    Assert.Contains("local:SegmentedTextBlockBehavior.Segments=\"{Binding OriginalSegments}\"", overlayXaml);
    Assert.Contains("local:SegmentedTextBlockBehavior.Segments=\"{Binding TranslationSegments}\"", overlayXaml);
    Assert.Contains("public bool IsCurrentBatchLine", overlayViewModel);
    Assert.Contains("private Guid? currentBatchLineId", overlayViewModel);
    Assert.Contains("currentBatchLineId = entry.Id;", overlayViewModel);
    Assert.Contains("line.SetCurrentBatchLine(IsCurrent && line.Id == currentBatchLineId)", overlayViewModel);
    Assert.True(!overlayXaml.Contains("Foreground=\"{Binding OriginalBrush}\" Text=\"{Binding OriginalDisplayText", StringComparison.Ordinal), "speaker-level combined text must not make all old lines active-colored.");
}

static void OverlayKeepsCurrentBatchColorThroughNonActivityRefresh()
{
    var overlayViewModel = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "ViewModels", "OverlaySpeakerViewModel.cs"));

    Assert.Contains("preserveCurrentBatchSegments: !refreshActivity", overlayViewModel);
    Assert.Contains("preserveCurrentBatchSegments", overlayViewModel);
    Assert.Contains("preserveExistingSegments", overlayViewModel);
    Assert.Contains("string.Equals(previousText, currentText, StringComparison.Ordinal)", overlayViewModel);
    Assert.Contains("collection.Count > 0", overlayViewModel);
    Assert.Contains("return;", overlayViewModel);
}

static void OverlaySupportsMultipleActiveSpeakersInOneUpdateBatch()
{
    var overlaySource = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "OverlayWindow.xaml.cs"));

    Assert.Contains("private static readonly TimeSpan ActiveBatchWindow", overlaySource);
    Assert.Contains("private readonly HashSet<string> activeSpeakerIds", overlaySource);
    Assert.Contains("private DateTime lastActiveSpeakerUpdateUtc", overlaySource);
    Assert.Contains("now - lastActiveSpeakerUpdateUtc > ActiveBatchWindow", overlaySource);
    Assert.Contains("activeSpeakerIds.Add(activeSpeaker.SpeakerId);", overlaySource);
    Assert.Contains("speaker.SetCurrent(activeSpeakerIds.Contains(speaker.SpeakerId));", overlaySource);
    Assert.Contains("if (refreshActivity)", overlaySource);
    Assert.Contains("MarkActiveSpeaker(speaker);", overlaySource);
    Assert.True(!overlaySource.Contains("speaker.SetCurrent(ReferenceEquals(speaker, currentSpeaker));", StringComparison.Ordinal), "Overlay must allow more than one recently updated speaker to keep the active color.");
}

static void OverlayExposesConfigurableColorSettings()
{
    var settingsSource = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "Models", "AppSettings.cs"));
    var mainXaml = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml"));
    var mainSource = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml.cs"));
    var overlayViewModel = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "ViewModels", "OverlaySpeakerViewModel.cs"));
    var overlayWindow = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "OverlayWindow.xaml.cs"));
    var colorWindowXaml = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "OverlayColorWindow.xaml"));
    var colorWindowSource = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "OverlayColorWindow.xaml.cs"));
    var localizerSource = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "Services", "Localizer.cs"));

    Assert.Contains("public string ActiveSpeakerColor", settingsSource);
    Assert.Contains("public string InactiveSpeakerColor", settingsSource);
    Assert.Contains("public string ActiveOriginalColor", settingsSource);
    Assert.Contains("public string ActiveTranslationColor", settingsSource);
    Assert.Contains("public string InactiveOriginalColor", settingsSource);
    Assert.Contains("public string InactiveTranslationColor", settingsSource);
    Assert.Contains("OverlayColorsButton", mainXaml);
    Assert.Contains("OverlayColorsButton_Click", mainXaml);
    Assert.Contains("new OverlayColorWindow(settings.Overlay, localizer)", mainSource);
    Assert.Contains("settings.Overlay = window.Settings;", mainSource);
    Assert.Contains("ApplyColors(OverlayWindowSettings settings)", overlayViewModel);
    Assert.Contains("public Brush SpeakerBrush", overlayViewModel);
    Assert.Contains("public Brush OriginalBrush", overlayViewModel);
    Assert.Contains("public Brush TranslationBrush", overlayViewModel);
    Assert.Contains("ApplyOverlayColors();", overlayWindow);
    Assert.Contains("ActiveSpeakerColorBox", colorWindowXaml);
    Assert.Contains("InactiveTranslationColorBox", colorWindowXaml);
    Assert.Contains("UpdateSwatch", colorWindowSource);
    Assert.Contains("OverlayColorRole", localizerSource);
}

static void OverlayBatchRefreshDoesNotResetInactivityTimer()
{
    var overlaySource = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "OverlayWindow.xaml.cs"));
    var overlayViewModel = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "ViewModels", "OverlaySpeakerViewModel.cs"));

    Assert.Contains("UpdateEntries(entries, linesPerSpeaker, allowCreateMissing: false);", overlaySource);
    Assert.Contains("SeedEntries(IEnumerable<CaptionEntryViewModel> entries, int linesPerSpeaker)", overlaySource);
    Assert.Contains("UpdateEntries(entries, linesPerSpeaker, allowCreateMissing: true);", overlaySource);
    Assert.Contains("UpdateEntry(CaptionEntryViewModel entry, int linesPerSpeaker, bool refreshActivity, bool allowCreateMissing)", overlaySource);
    Assert.Contains("if (speaker == null && !allowCreateMissing)", overlaySource);
    Assert.Contains("speaker.Apply(entry, linesPerSpeaker, refreshActivity);", overlaySource);
    Assert.Contains("Apply(CaptionEntryViewModel entry, int linesPerSpeaker, bool refreshActivity = true)", overlayViewModel);
    Assert.Contains("if (refreshActivity)", overlayViewModel);
    Assert.Contains("LastUpdatedUtc = DateTime.UtcNow;", overlayViewModel);
}

static void OverlayPersistsLayoutAndExposesResetAction()
{
    var settingsSource = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "Models", "AppSettings.cs"));
    var mainXaml = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml"));
    var mainSource = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml.cs"));
    var overlayXaml = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "OverlayWindow.xaml"));
    var overlaySource = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "OverlayWindow.xaml.cs"));
    var localizerSource = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "Services", "Localizer.cs"));

    Assert.Contains("public OverlayWindowSettings Overlay", settingsSource);
    Assert.Contains("public bool OverlayOpen", settingsSource);
    Assert.Contains("public double Width", settingsSource);
    Assert.Contains("public double FontSize", settingsSource);
    Assert.Contains("public double Opacity", settingsSource);
    Assert.Contains("public bool ShowBorder", settingsSource);
    Assert.Contains("ResetOverlayButton", mainXaml);
    Assert.Contains("ResetOverlayButton_Click", mainXaml);
    Assert.Contains("OverlayOpacitySlider", mainXaml);
    Assert.Contains("OverlayOpacityLabel", mainXaml);
    Assert.Contains("BorderToggleButton", overlayXaml);
    Assert.Contains("OverlayOpacityControlSlider", overlayXaml);
    Assert.Contains("OverlayResetButton", overlayXaml);
    Assert.Contains("new OverlayWindow(localizer, settings.Overlay)", mainSource);
    Assert.Contains("overlayWindow.SeedEntries(LiveEntryViewModels(), settings.OverlayDisplayLines);", mainSource);
    Assert.Contains("OverlaySettingsChanged += OverlayWindow_SettingsChanged", mainSource);
    Assert.Contains("settings.Overlay = OverlayWindowSettings.Default();", mainSource);
    Assert.Contains("Minimum=\"0\"", mainXaml);
    Assert.Contains("Minimum=\"0\"", overlayXaml);
    Assert.Contains("settings.Overlay.Opacity = Math.Clamp(OverlayOpacitySlider.Value / 100.0, 0.0, 1.0);", mainSource);
    Assert.Contains("overlayWindow?.ApplyOverlaySettings(settings.Overlay);", mainSource);
    Assert.Contains("public event EventHandler<OverlayWindowSettings>? OverlaySettingsChanged", overlaySource);
    Assert.Contains("ApplyOverlaySettings(OverlayWindowSettings settings)", overlaySource);
    Assert.Contains("CaptureOverlaySettings()", overlaySource);
    Assert.Contains("OverlayFrame.Background", overlaySource);
    Assert.Contains("CreateOverlayBackgroundBrush", overlaySource);
    Assert.Contains("Math.Clamp(opacity, 0.0, 1.0)", overlaySource);
    Assert.Contains("byte.MaxValue * NormalizeOpacity(opacity)", overlaySource);
    Assert.Contains("BorderToggle_Click", overlaySource);
    Assert.Contains("OverlayOpacityControlSlider_ValueChanged", overlaySource);
    Assert.Contains("OverlayResetButton_Click", overlaySource);
    Assert.True(!overlaySource.Contains("OverlayFrame.Opacity =", StringComparison.Ordinal), "Overlay opacity must not fade caption text.");
    Assert.Contains("OverlayReset", localizerSource);
    Assert.Contains("OverlayOpacity", localizerSource);
    Assert.Contains("OverlayBorder", localizerSource);
}

static void OverlayIgnoresOpacityEventsBeforeTemplateLoadCompletes()
{
    var overlaySource = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "OverlayWindow.xaml.cs"));

    Assert.Contains("OverlayTemplateReady", overlaySource);
    Assert.Contains("if (!OverlayTemplateReady)", overlaySource);
    Assert.Contains("OverlayOpacityControlSlider_ValueChanged", overlaySource);
    Assert.True(
        overlaySource.IndexOf("if (!OverlayTemplateReady)", StringComparison.Ordinal) <
        overlaySource.IndexOf("currentSettings.Opacity = NormalizeOpacity(OverlayOpacityControlSlider.Value / 100.0);", StringComparison.Ordinal),
        "overlay opacity slider can raise during XAML construction before later named controls are assigned.");
}

static void OverlayExposesPersistedClickThroughSetting()
{
    var settingsSource = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "Models", "AppSettings.cs"));
    var mainXaml = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml"));
    var mainSource = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml.cs"));
    var overlaySource = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "OverlayWindow.xaml.cs"));
    var colorWindowSource = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "OverlayColorWindow.xaml.cs"));
    var localizerSource = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "Services", "Localizer.cs"));

    Assert.Contains("public bool ClickThrough", settingsSource);
    Assert.Contains("x:Name=\"OverlayClickThroughCheck\"", mainXaml);
    Assert.Contains("OverlayClickThroughCheck.Content = L(\"ClickThrough\")", mainSource);
    Assert.Contains("OverlayClickThroughCheck.ToolTip = L(\"ClickThroughHelp\")", mainSource);
    Assert.Contains("OverlayClickThroughCheck.IsChecked = settings.Overlay.ClickThrough;", mainSource);
    Assert.Contains("settings.Overlay.ClickThrough = OverlayClickThroughCheck.IsChecked == true;", mainSource);
    Assert.Contains("currentSettings.ClickThrough = settings.ClickThrough;", overlaySource);
    Assert.Contains("ApplyClickThrough(currentSettings.ClickThrough);", overlaySource);
    Assert.Contains("ClickThrough = currentSettings.ClickThrough", overlaySource);
    Assert.Contains("CreateOverlayBackgroundBrush(currentSettings.Opacity, currentSettings.ClickThrough)", overlaySource);
    Assert.Contains("(byte)Math.Max(1, (int)Math.Round(byte.MaxValue * NormalizeOpacity(opacity)))", overlaySource);
    Assert.Contains("ClickThrough = settings.ClickThrough", colorWindowSource);
    Assert.Contains("[\"ClickThroughHelp\"]", localizerSource);
}

static void OverlayOpenStateRestoresOnStartup()
{
    var settingsSource = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "Models", "AppSettings.cs"));
    var mainSource = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml.cs"));

    Assert.Contains("public bool OverlayOpen", settingsSource);
    Assert.Contains("Loaded += MainWindow_Loaded;", mainSource);
    Assert.Contains("private async void MainWindow_Loaded(object sender, RoutedEventArgs e)", mainSource);
    Assert.Contains("if (settings.OverlayOpen)", mainSource);
    Assert.Contains("ShowOverlay(rememberOpen: false);", mainSource);
    Assert.Contains("ShowOverlay(rememberOpen: true);", mainSource);
    Assert.Contains("CloseOverlay(rememberClosed: true);", mainSource);
    Assert.Contains("private void OverlayWindow_Closed(object? sender, EventArgs e)", mainSource);
    Assert.Contains("if (!closingApp && settings.OverlayOpen)", mainSource);
    Assert.Contains("settings.OverlayOpen = overlayWindow.IsVisible;", mainSource);
}

static void OverlayAutoSizesHeightWithBottomAnchored()
{
    var settingsSource = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "Models", "AppSettings.cs"));
    var overlaySource = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "OverlayWindow.xaml.cs"));

    Assert.Contains("public bool AutoHeight", settingsSource);
    Assert.Contains("AdjustHeightToContent();", overlaySource);
    Assert.Contains("var bottom = Top +", overlaySource);
    Assert.Contains("Top = bottom - nextHeight;", overlaySource);
    Assert.Contains("Dispatcher.BeginInvoke", overlaySource);
    Assert.Contains("settings.AutoHeight", overlaySource);
}

static void MainWindowRemovesSpeakerRenameFeature()
{
    var mainXaml = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml"));
    var mainSource = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml.cs"));
    var settingsStoreSource = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "Services", "SettingsStore.cs"));

    Assert.True(!mainXaml.Contains("RenameButton", StringComparison.Ordinal), "speaker rename button must be removed from settings UI");
    Assert.True(!mainXaml.Contains("SpeakerNamesLabel", StringComparison.Ordinal), "speaker name settings label must be removed");
    Assert.True(!mainSource.Contains("RenameButton_Click", StringComparison.Ordinal), "speaker rename click handler must be removed");
    Assert.True(!mainSource.Contains("SpeakerRenameWindow", StringComparison.Ordinal), "speaker rename window must not be opened");
    Assert.True(!mainSource.Contains("settings.SpeakerNames", StringComparison.Ordinal), "saved speaker rename mappings must not affect displayed names");
    Assert.True(settingsStoreSource.Contains("new Dictionary<string, string>()", StringComparison.Ordinal), "worker configuration must ignore persisted speaker rename mappings");
    Assert.True(!File.Exists(Path.Combine("src", "LiveDialogueTranslator.App", "SpeakerRenameWindow.xaml")), "speaker rename window xaml must be removed");
    Assert.True(!File.Exists(Path.Combine("src", "LiveDialogueTranslator.App", "SpeakerRenameWindow.xaml.cs")), "speaker rename window code-behind must be removed");
}

static void WorkerEnvironmentInstallsCudaTorchWhenNvidiaGpuIsPresent()
{
    var source = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "Services", "WorkerEnvironmentService.cs"));

    Assert.Contains("EnsureCudaAccelerationAsync(settings, token)", source);
    Assert.Contains("NvidiaGpuAvailableAsync(token)", source);
    Assert.Contains("TorchCudaAvailableAsync(token)", source);
    Assert.Contains("PythonPipCommands.InstallCudaTorchArguments()", source);
}

static void WorkerEnvironmentBlocksStartWhenHfModelAccessFails()
{
    var environment = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "Services", "WorkerEnvironmentService.cs"));
    var mainWindow = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml.cs"));
    var hints = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.Core", "Startup", "SetupActionHints.cs"));

    Assert.Contains("CheckHuggingFaceAccessAsync(settings, token)", environment);
    Assert.Contains("StartupCapability.NeedsHuggingFaceAccess", environment);
    Assert.Contains("HandleDiarizationAccessFailureAsync(error)", mainWindow);
    Assert.Contains("error.Code.Equals(\"hf_access_denied\"", mainWindow);
    Assert.Contains("code.Equals(\"hf_access_denied\"", hints);
}

static void MainWindowStopsCaptureOnFatalWorkerError()
{
    var source = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml.cs"));

    Assert.Contains("if (!error.Recoverable)", source);
    Assert.Contains("_ = HandleFatalWorkerErrorAsync(error);", source);
    Assert.Contains("private async Task HandleFatalWorkerErrorAsync(WorkerErrorEvent error)", source);
    Assert.Contains("await StopCaptureAsync(showStopped: false);", source);
    Assert.Contains("SetCaptureButtonRunning(false);", source);
    Assert.Contains("HideSetupProgressIfReady();", source);
    Assert.Contains("ShowCaptionDetail(L(\"WorkerError\")", source);
}

static void WorkerEnvironmentSkipsLocalDiarizationPackageInstallsForWhisperLiveKit()
{
    var source = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "Services", "WorkerEnvironmentService.cs"));

    Assert.Contains("settings.DiarizationModel != DiarizationModel.Diart", source);
    Assert.Contains("speechSeparationModel != SpeechSeparationModel.None", source);
    Assert.Contains("engines.Contains(AsrEngine.WhisperLiveKitSortformer)", source);
}

static void SetupActionHintsExposeInstallActionForMockMode()
{
    var hint = SetupActionHints.ForModelStatus("mock_mode", "Install worker requirements to enable local STT.");

    Assert.Equal(SetupActionKind.InstallWorker, hint.Kind);
    Assert.Equal("Install", hint.Label);
}

static void SetupActionHintsExposeTokenActionForHfTokenErrors()
{
    var warningHint = SetupActionHints.ForWarning("Hugging Face token is missing.");
    var errorHint = SetupActionHints.ForWorkerError("hf_token_missing", "Hugging Face token is required.");
    var setupFailureHint = SetupActionHints.ForSetupFailure("""{"type":"error","code":"hf_token_missing","message":"Hugging Face token is required."}""");

    Assert.Equal(SetupActionKind.HuggingFaceToken, warningHint.Kind);
    Assert.Equal("Set Access", warningHint.Label);
    Assert.Equal(SetupActionKind.HuggingFaceToken, errorHint.Kind);
    Assert.Equal(SetupActionKind.HuggingFaceToken, setupFailureHint.Kind);

    var gatedHint = SetupActionHints.ForSetupFailure("Access to model pyannote/speaker-diarization-community-1 is restricted. You must accept the user agreement.");
    Assert.Equal(SetupActionKind.HuggingFaceToken, gatedHint.Kind);
    Assert.Equal("Set Access", gatedHint.Label);

    var unauthorizedHint = SetupActionHints.ForSetupFailure("You are not in the authorized list to access this gated model.");
    Assert.Equal(SetupActionKind.HuggingFaceToken, unauthorizedHint.Kind);
    Assert.Equal("Set Access", unauthorizedHint.Label);

    var diartWorkerHint = SetupActionHints.ForWorkerError("hf_access_denied", "Cannot access gated repo for pyannote/segmentation.");
    Assert.Equal(SetupActionKind.HuggingFaceToken, diartWorkerHint.Kind);
    Assert.Equal("Set Access", diartWorkerHint.Label);
}

static void StartupPlannerInstallsPackagesBeforePreparingModels()
{
    var plan = WorkerStartupPlanner.CreatePlan(new WorkerStartupState(
        PythonAvailable: true,
        LocalWhisperRequested: true,
        FasterWhisperAvailable: false,
        PyannoteAvailable: false,
        DiartAvailable: false,
        TorchAvailable: true,
        SttModelPrepared: false,
        SttModelLoadable: false,
        DiarizationModelPrepared: false,
        DiarizationRequested: true,
        DiarizationModel: DiarizationModel.PyannoteCommunity,
        AsrEngine: AsrEngine.None,
        QwenAsrAvailable: false,
        WhisperLiveKitAvailable: false,
        WhisperXAvailable: false,
        HasHuggingFaceToken: true));

    Assert.Equal(StartupActionKind.InstallPythonPackages, plan.Actions[0].Kind);
    Assert.Equal(StartupActionKind.PrepareModels, plan.Actions[1].Kind);
    Assert.Equal(StartupCapability.FullDiarization, plan.Capability);
}

static void StartupPlannerPreparesModelsWhenCachedSttModelCannotLoad()
{
    var plan = WorkerStartupPlanner.CreatePlan(new WorkerStartupState(
        PythonAvailable: true,
        LocalWhisperRequested: true,
        FasterWhisperAvailable: true,
        PyannoteAvailable: true,
        DiartAvailable: false,
        TorchAvailable: true,
        SttModelPrepared: true,
        SttModelLoadable: false,
        DiarizationModelPrepared: true,
        DiarizationRequested: true,
        DiarizationModel: DiarizationModel.PyannoteCommunity,
        AsrEngine: AsrEngine.None,
        QwenAsrAvailable: false,
        WhisperLiveKitAvailable: false,
        WhisperXAvailable: false,
        HasHuggingFaceToken: true));

    Assert.Equal(1, plan.Actions.Count);
    Assert.Equal(StartupActionKind.PrepareModels, plan.Actions[0].Kind);
}

static void StartupPlannerAllowsCachedLocalDiarizationWithoutHfToken()
{
    var plan = WorkerStartupPlanner.CreatePlan(new WorkerStartupState(
        PythonAvailable: true,
        LocalWhisperRequested: true,
        FasterWhisperAvailable: true,
        PyannoteAvailable: true,
        DiartAvailable: false,
        TorchAvailable: true,
        SttModelPrepared: true,
        SttModelLoadable: true,
        DiarizationModelPrepared: true,
        DiarizationRequested: true,
        DiarizationModel: DiarizationModel.PyannoteCommunity,
        AsrEngine: AsrEngine.None,
        QwenAsrAvailable: false,
        WhisperLiveKitAvailable: false,
        WhisperXAvailable: false,
        HasHuggingFaceToken: false));

    Assert.Equal(StartupCapability.FullDiarization, plan.Capability);
    Assert.Equal(0, plan.Actions.Count);
    Assert.Equal(null, plan.Warning);
}

static void StartupPlannerRequestsHuggingFaceAccessBeforeLocalDiarization()
{
    var plan = WorkerStartupPlanner.CreatePlan(new WorkerStartupState(
        PythonAvailable: true,
        LocalWhisperRequested: true,
        FasterWhisperAvailable: true,
        PyannoteAvailable: true,
        DiartAvailable: false,
        TorchAvailable: true,
        SttModelPrepared: true,
        SttModelLoadable: true,
        DiarizationModelPrepared: false,
        DiarizationRequested: true,
        DiarizationModel: DiarizationModel.PyannoteCommunity,
        AsrEngine: AsrEngine.None,
        QwenAsrAvailable: false,
        WhisperLiveKitAvailable: false,
        WhisperXAvailable: false,
        HasHuggingFaceToken: false));

    Assert.Equal(StartupCapability.NeedsHuggingFaceAccess, plan.Capability);
    Assert.Contains("pyannote model files", plan.Warning ?? "");
    Assert.Equal(0, plan.Actions.Count);
}

static void WorkerEnvironmentChecksHfAccessBeforeSetupWhenModelsNeedDownload()
{
    var environment = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "Services", "WorkerEnvironmentService.cs"));

    var accessCheck = environment.IndexOf("var earlyAccessError = await CheckHuggingFaceAccessAsync(settings, token);", StringComparison.Ordinal);
    var packageInstall = environment.IndexOf("await InstallPackagesAsync(token);", StringComparison.Ordinal);
    var cudaInstall = environment.IndexOf("await EnsureCudaAccelerationAsync(settings, token);", StringComparison.Ordinal);
    var modelPrepare = environment.IndexOf("await PrepareModelsAsync(settings, effectiveSpeechSeparationModel, token);", StringComparison.Ordinal);

    Assert.True(accessCheck >= 0, "HF access must be checked before setup when missing models require gated downloads.");
    Assert.True(packageInstall > accessCheck, "HF access check must happen before package installation.");
    Assert.True(cudaInstall > accessCheck, "HF access check must happen before CUDA setup.");
    Assert.True(modelPrepare > accessCheck, "HF access check must happen before model preparation.");
    Assert.Contains("RequiresHuggingFaceAccessBeforeSetup(settings, effectiveSpeechSeparationModel, plan)", environment);
    Assert.Contains("NeedsModelPreparation(plan)", environment);
    Assert.Contains("RequiredHuggingFaceModelIds(settings.DiarizationModel)", environment);
}

static void StartupPlannerInstallsSelectedAsrEnginePackages()
{
    var plan = WorkerStartupPlanner.CreatePlan(new WorkerStartupState(
        PythonAvailable: true,
        LocalWhisperRequested: true,
        FasterWhisperAvailable: true,
        PyannoteAvailable: true,
        DiartAvailable: false,
        TorchAvailable: true,
        SttModelPrepared: true,
        SttModelLoadable: true,
        DiarizationModelPrepared: true,
        DiarizationRequested: true,
        DiarizationModel: DiarizationModel.PyannoteCommunity,
        AsrEngine: AsrEngine.Qwen3Asr,
        QwenAsrAvailable: false,
        WhisperLiveKitAvailable: false,
        WhisperXAvailable: false,
        HasHuggingFaceToken: true));

    Assert.Equal(StartupActionKind.InstallPythonPackages, plan.Actions[0].Kind);

    var whisperXPlan = WorkerStartupPlanner.CreatePlan(new WorkerStartupState(
        PythonAvailable: true,
        LocalWhisperRequested: true,
        FasterWhisperAvailable: true,
        PyannoteAvailable: true,
        DiartAvailable: false,
        TorchAvailable: true,
        SttModelPrepared: true,
        SttModelLoadable: true,
        DiarizationModelPrepared: true,
        DiarizationRequested: false,
        DiarizationModel: DiarizationModel.PyannoteCommunity,
        AsrEngine: AsrEngine.WhisperX,
        QwenAsrAvailable: true,
        WhisperLiveKitAvailable: true,
        WhisperXAvailable: false,
        HasHuggingFaceToken: false));

    Assert.Equal(StartupActionKind.InstallPythonPackages, whisperXPlan.Actions[0].Kind);
}

static void SpeechSeparationAdvisorRecommendsModelsByHardware()
{
    var highMemory = new HardwareProfile(
        "CPU",
        16,
        32 * SpeechSeparationAdvisor.GiB,
        "NVIDIA GeForce RTX",
        12 * SpeechSeparationAdvisor.GiB,
        true);
    var lowerMemory = highMemory with { GpuMemoryBytes = 8 * SpeechSeparationAdvisor.GiB };

    var highRecommendation = SpeechSeparationAdvisor.Recommend(highMemory, ComputeMode.Auto, AsrEngine.None);
    var lowerRecommendation = SpeechSeparationAdvisor.Recommend(lowerMemory, ComputeMode.Cuda, AsrEngine.WhisperX);
    var qwenHeavyRecommendation = SpeechSeparationAdvisor.Recommend(
        highMemory,
        ComputeMode.Cuda,
        AsrEngine.Qwen3Asr,
        "qwen3-asr-1.7b");

    Assert.Equal(SpeechSeparationModel.MossFormer2, highRecommendation.Model);
    Assert.Equal(2, highRecommendation.SupportedModels.Count);
    Assert.Equal(SpeechSeparationModel.SepFormerWhamr16k, lowerRecommendation.Model);
    Assert.Equal(1, lowerRecommendation.SupportedModels.Count);
    Assert.Equal(SpeechSeparationModel.None, qwenHeavyRecommendation.Model);
}

static void SpeechSeparationAdvisorRejectsUnsupportedRuntimePaths()
{
    var profile = new HardwareProfile(
        "CPU",
        16,
        32 * SpeechSeparationAdvisor.GiB,
        "NVIDIA GeForce RTX",
        12 * SpeechSeparationAdvisor.GiB,
        true);
    var cpu = SpeechSeparationAdvisor.Recommend(profile, ComputeMode.Cpu, AsrEngine.None);
    var streaming = SpeechSeparationAdvisor.Recommend(profile, ComputeMode.Cuda, AsrEngine.WhisperLiveKitSortformer);
    var noGpu = SpeechSeparationAdvisor.Recommend(profile with { GpuName = null, NvidiaDriverAvailable = false }, ComputeMode.Auto, AsrEngine.None);

    Assert.Equal(SpeechSeparationModel.None, cpu.Model);
    Assert.Equal(SpeechSeparationModel.None, streaming.Model);
    Assert.Equal(SpeechSeparationModel.None, noGpu.Model);
    Assert.Equal(SpeechSeparationModel.None, SpeechSeparationAdvisor.Resolve(SpeechSeparationModel.MossFormer2, noGpu));
}

static void StartupPlannerInstallsAndPreparesSpeechSeparation()
{
    var plan = WorkerStartupPlanner.CreatePlan(new WorkerStartupState(
        PythonAvailable: true,
        LocalWhisperRequested: true,
        FasterWhisperAvailable: true,
        PyannoteAvailable: true,
        DiartAvailable: true,
        TorchAvailable: true,
        SttModelPrepared: true,
        SttModelLoadable: true,
        DiarizationModelPrepared: true,
        DiarizationRequested: false,
        DiarizationModel: DiarizationModel.PyannoteCommunity,
        AsrEngine: AsrEngine.None,
        QwenAsrAvailable: true,
        WhisperLiveKitAvailable: true,
        WhisperXAvailable: true,
        HasHuggingFaceToken: false,
        SpeechSeparationModel: SpeechSeparationModel.MossFormer2,
        SpeechSeparationPackageAvailable: false,
        SpeechSeparationModelPrepared: false));

    Assert.Equal(2, plan.Actions.Count);
    Assert.Equal(StartupActionKind.InstallPythonPackages, plan.Actions[0].Kind);
    Assert.Equal(StartupActionKind.PrepareModels, plan.Actions[1].Kind);
    Assert.Equal(StartupCapability.SpeechSeparation, plan.Capability);
}

static void MainWindowExposesAutomaticHardwareBasedSpeechSeparation()
{
    var xaml = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml"));
    var source = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "MainWindow.xaml.cs"));
    var detector = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "Services", "HardwareDetectionService.cs"));

    Assert.Contains("x:Name=\"SpeechSeparationModelBox\"", xaml);
    Assert.Contains("x:Name=\"HardwareSummaryText\"", xaml);
    Assert.Contains("x:Name=\"RedetectHardwareButton\"", xaml);
    Assert.Contains("await hardwareDetection.DetectAsync()", source);
    Assert.Contains("SpeechSeparationAdvisor.Recommend", source);
    Assert.Contains("sender == SttModelBox", source);
    Assert.Contains("nvidia-smi", detector);
    Assert.Contains("GlobalMemoryStatusEx", detector);
}

static void SpeechSeparationRequirementsOnlyExposeIntegratedModels()
{
    var moss = File.ReadAllText(Path.Combine("worker", "requirements-speech-separation-mossformer2.txt"));
    var sep = File.ReadAllText(Path.Combine("worker", "requirements-speech-separation-sepformer.txt"));
    var environment = File.ReadAllText(Path.Combine("src", "LiveDialogueTranslator.App", "Services", "SpeechSeparationEnvironment.cs"));

    Assert.Contains("clearvoice==0.1.2", moss);
    Assert.Contains("speechbrain==1.0.3", sep);
    Assert.Contains("requirements-speech-separation-mossformer2.txt", environment);
    Assert.Contains("requirements-speech-separation-sepformer.txt", environment);
    Assert.True(!environment.Contains("RE-SepFormer", StringComparison.OrdinalIgnoreCase), "Unintegrated research models must not appear as selectable runtimes.");
    Assert.True(!environment.Contains("TF-GridNet", StringComparison.OrdinalIgnoreCase), "Unintegrated research models must not appear as selectable runtimes.");
}

static class Assert
{
    public static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
        }
    }

    public static void Contains(string expected, string actual)
    {
        if (!actual.Contains(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected '{actual}' to contain '{expected}'.");
        }
    }

    public static T IsType<T>(object value)
    {
        if (value is not T typed)
        {
            throw new InvalidOperationException($"Expected {typeof(T).Name}, got {value.GetType().Name}.");
        }

        return typed;
    }
}
