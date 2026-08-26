using System.Diagnostics;
using System.IO;
using System.Windows;
using LiveDialogueTranslator.App.Models;
using LiveDialogueTranslator.App.Services;
using LiveDialogueTranslator.Core.Protocol;
using LiveDialogueTranslator.Core.Runtime;
using LiveDialogueTranslator.Core.Startup;

namespace LiveDialogueTranslator.App;

public partial class ModelManagerWindow : Window
{
    private readonly AppPaths paths;
    private readonly AppSettings settings;
    private readonly Localizer localizer;
    private readonly PythonRuntimeService pythonRuntime;
    private readonly SpeechSeparationModel effectiveSpeechSeparationModel;

    public ModelManagerWindow(
        AppPaths paths,
        AppSettings settings,
        Localizer localizer,
        SpeechSeparationModel effectiveSpeechSeparationModel,
        bool showAccessNotice = false)
    {
        InitializeComponent();
        this.paths = paths;
        this.settings = settings;
        this.localizer = localizer;
        this.effectiveSpeechSeparationModel = effectiveSpeechSeparationModel;
        pythonRuntime = new PythonRuntimeService(paths, localizer);
        ApplyLocalization();
        TokenBox.Password = settings.HuggingFaceToken ?? "";
        OutputBox.Text = showAccessNotice
            ? L("AccessNoticeExpanded")
            : LF("ModelDirectory", Environment.NewLine, paths.ModelDirectory);
    }

    private void ApplyLocalization()
    {
        Title = L("ModelManagerTitle");
        HeaderText.Text = L("LocalModelSetup");
        DescriptionText.Text = L("ModelStoredDetail");
        AccessNoticeText.Text = L("AccessNotice");
        TokenLabel.Text = L("HfToken");
        GetTokenButton.Content = L("GetToken");
        GetTokenButton.ToolTip = L("OpenTokenPage");
        CheckAccessButton.Content = L("CheckAccess");
        ModelTermsLabel.Text = L("ModelTerms");
        CommunityTermsButton.Content = L("CommunityTerms");
        CommunityTermsButton.ToolTip = L("OpenCommunityModelPage");
        DiartSegmentationTermsButton.Content = L("DiartSegmentationTerms");
        DiartSegmentationTermsButton.ToolTip = L("OpenDiartSegmentationPage");
        DiartEmbeddingTermsButton.Content = L("DiartEmbeddingTerms");
        DiartEmbeddingTermsButton.ToolTip = L("OpenDiartEmbeddingPage");
        OpenFolderButton.Content = L("OpenFolder");
        PrepareButton.Content = L("Prepare");
        SaveButton.Content = L("Save");
        UpdateModelTermsButtons();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = paths.ModelDirectory,
            UseShellExecute = true
        });
    }

    private void OpenTokenPage_Click(object sender, RoutedEventArgs e)
    {
        ExternalLinkService.OpenUrl(HuggingFaceLinks.AccessTokensUrl);
    }

    private void OpenCommunityTerms_Click(object sender, RoutedEventArgs e)
    {
        ExternalLinkService.OpenUrl(HuggingFaceLinks.CommunityModelUrl);
    }

    private void OpenDiartSegmentationTerms_Click(object sender, RoutedEventArgs e)
    {
        ExternalLinkService.OpenUrl(HuggingFaceLinks.DiartSegmentationModelUrl);
    }

    private void OpenDiartEmbeddingTerms_Click(object sender, RoutedEventArgs e)
    {
        ExternalLinkService.OpenUrl(HuggingFaceLinks.DiartEmbeddingModelUrl);
    }

    private void UpdateModelTermsButtons()
    {
        if (!RequiresHuggingFaceAccess())
        {
            CommunityTermsButton.Visibility = Visibility.Collapsed;
            DiartSegmentationTermsButton.Visibility = Visibility.Collapsed;
            DiartEmbeddingTermsButton.Visibility = Visibility.Collapsed;
            return;
        }

        var usesDiart = settings.DiarizationModel == DiarizationModel.Diart;
        CommunityTermsButton.Visibility = usesDiart ? Visibility.Collapsed : Visibility.Visible;
        DiartSegmentationTermsButton.Visibility = usesDiart ? Visibility.Visible : Visibility.Collapsed;
        DiartEmbeddingTermsButton.Visibility = usesDiart ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void CheckAccessButton_Click(object sender, RoutedEventArgs e)
    {
        await CheckAccessAsync();
    }

    private async void PrepareButton_Click(object sender, RoutedEventArgs e)
    {
        settings.HuggingFaceToken = TokenValue();
        if (!File.Exists(paths.WorkerScriptPath))
        {
            OutputBox.Text = LF("WorkerMissing", paths.WorkerScriptPath);
            return;
        }

        try
        {
            if (RequiresHuggingFaceAccess() && !await CheckAccessAsync())
            {
                return;
            }

            var pythonExe = await pythonRuntime.EnsureAsync(
                report: (title, detail, _) => OutputBox.Text = $"{title}{Environment.NewLine}{detail}");
            if (effectiveSpeechSeparationModel == SpeechSeparationModel.None &&
                settings.DiarizationModel == DiarizationModel.Diart &&
                !await InstallDiartAsync(pythonExe))
            {
                return;
            }
            if (!await InstallAsrEnginePackagesAsync(pythonExe))
            {
                return;
            }
            if (!await InstallSpeechSeparationPackagesAsync(pythonExe))
            {
                return;
            }

            var psi = new ProcessStartInfo
            {
                FileName = pythonExe,
                Arguments = $"\"{paths.WorkerScriptPath}\" --download --models \"{paths.ModelDirectory}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            PythonProcessEnvironment.Apply(psi.Environment);
            AsrEngineEnvironment.Apply(
                psi.Environment,
                paths,
                settings.AsrEngine,
                settings.DiarizationModel,
                effectiveSpeechSeparationModel == SpeechSeparationModel.None && settings.DiarizationEnabled);
            SpeechSeparationEnvironment.Apply(psi.Environment, paths, effectiveSpeechSeparationModel);
            if (!string.IsNullOrWhiteSpace(settings.HuggingFaceToken))
            {
                psi.Environment["HF_TOKEN"] = settings.HuggingFaceToken;
            }
            psi.Environment["LIVE_DIALOGUE_TRANSLATOR_STT_MODEL"] = settings.SttModel;
            psi.Environment["LIVE_DIALOGUE_TRANSLATOR_DIARIZATION_ENABLED"] =
                effectiveSpeechSeparationModel == SpeechSeparationModel.None && settings.DiarizationEnabled
                    ? "true"
                    : "false";
            psi.Environment["LIVE_DIALOGUE_TRANSLATOR_DIARIZATION_MODEL"] = WorkerProtocol.FormatDiarizationModel(settings.DiarizationModel);
            psi.Environment["LIVE_DIALOGUE_TRANSLATOR_ASR_ENGINE"] = WorkerProtocol.FormatAsrEngine(settings.AsrEngine);
            psi.Environment["LIVE_DIALOGUE_TRANSLATOR_STT_QUALITY_PRESET"] = settings.SttQualityPreset.ToString();

            using var process = Process.Start(psi);
            if (process == null)
            {
                OutputBox.Text = L("UnableToStartPython");
                return;
            }

            var output = await process.StandardOutput.ReadToEndAsync();
            var error = FilterBenignStderr(await process.StandardError.ReadToEndAsync());
            await process.WaitForExitAsync();
            OutputBox.Text = string.IsNullOrWhiteSpace(error) ? output : output + Environment.NewLine + error;
        }
        catch (Exception ex)
        {
            OutputBox.Text = ex.Message;
        }
    }

    private async Task<bool> InstallDiartAsync(string pythonExe)
    {
        OutputBox.Text = L("InstallingDiartPackage");
        var psi = new ProcessStartInfo
        {
            FileName = pythonExe,
            Arguments = PythonPipCommands.InstallDiartArguments(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null)
        {
            OutputBox.Text = L("UnableToStartPython");
            return false;
        }

        var output = await process.StandardOutput.ReadToEndAsync();
        var error = FilterBenignStderr(await process.StandardError.ReadToEndAsync());
        await process.WaitForExitAsync();
        if (process.ExitCode == 0)
        {
            return true;
        }

        OutputBox.Text = $"{L("PythonPackageInstallFailed")}{Environment.NewLine}{output}{Environment.NewLine}{error}";
        return false;
    }

    private async Task<bool> InstallAsrEnginePackagesAsync(string pythonExe)
    {
        var engines = AsrEngineEnvironment.RequiredAsrEngines(
            settings.AsrEngine,
            settings.DiarizationModel,
            effectiveSpeechSeparationModel == SpeechSeparationModel.None && settings.DiarizationEnabled);
        if (engines.Count == 0)
        {
            return true;
        }

        foreach (var engine in engines)
        {
            var requirementsPath = AsrEngineEnvironment.RequirementsPath(engine);
            if (!File.Exists(requirementsPath))
            {
                OutputBox.Text = $"ASR engine requirements file not found: {requirementsPath}";
                return false;
            }

            var targetDirectory = paths.AsrPackageDirectory(engine);
            var stagingDirectory = PackageInstallStamp.CreateStagingDirectory(targetDirectory);
            try
            {
                OutputBox.Text = $"{L("InstallingAsrEnginePackages")}{Environment.NewLine}{L("AsrEnginePackagesCanTakeMinutes")}";
                var psi = new ProcessStartInfo
                {
                    FileName = pythonExe,
                    Arguments = PythonPipCommands.InstallRequirementsToTargetArguments(
                        requirementsPath,
                        stagingDirectory,
                        includeCudaTorchIndex: engine == AsrEngine.WhisperX),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                PythonProcessEnvironment.Apply(psi.Environment);
                AsrEngineEnvironment.Apply(
                    psi.Environment,
                    paths,
                    settings.AsrEngine,
                    settings.DiarizationModel,
                    effectiveSpeechSeparationModel == SpeechSeparationModel.None && settings.DiarizationEnabled);

                using var process = Process.Start(psi);
                if (process == null)
                {
                    OutputBox.Text = L("UnableToStartPython");
                    return false;
                }

                var output = await process.StandardOutput.ReadToEndAsync();
                var error = FilterBenignStderr(await process.StandardError.ReadToEndAsync());
                await process.WaitForExitAsync();
                if (process.ExitCode != 0)
                {
                    OutputBox.Text = $"{L("PythonPackageInstallFailed")}{Environment.NewLine}{output}{Environment.NewLine}{error}";
                    return false;
                }

                PackageInstallStamp.MarkCurrent(requirementsPath, stagingDirectory);
                PackageInstallStamp.CommitStagingDirectory(stagingDirectory, targetDirectory);
            }
            finally
            {
                PackageInstallStamp.DeleteStagingDirectory(stagingDirectory, targetDirectory);
            }
        }

        return true;
    }

    private async Task<bool> InstallSpeechSeparationPackagesAsync(string pythonExe)
    {
        if (effectiveSpeechSeparationModel is SpeechSeparationModel.None or SpeechSeparationModel.Auto)
        {
            return true;
        }

        var requirementsPath = SpeechSeparationEnvironment.RequirementsPath(effectiveSpeechSeparationModel);
        if (!File.Exists(requirementsPath))
        {
            OutputBox.Text = $"Speech separation requirements file not found: {requirementsPath}";
            return false;
        }

        var targetDirectory = paths.SpeechSeparationPackageDirectory(effectiveSpeechSeparationModel);
        var stagingDirectory = PackageInstallStamp.CreateStagingDirectory(targetDirectory);
        try
        {
            OutputBox.Text = $"{L("InstallingSpeechSeparationPackages")}{Environment.NewLine}{L("SpeechSeparationPackagesCanTakeMinutes")}";
            var psi = new ProcessStartInfo
            {
                FileName = pythonExe,
                Arguments = PythonPipCommands.InstallRequirementsToTargetArguments(requirementsPath, stagingDirectory),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            PythonProcessEnvironment.Apply(psi.Environment);
            AsrEngineEnvironment.Apply(
                psi.Environment,
                paths,
                settings.AsrEngine,
                settings.DiarizationModel,
                effectiveSpeechSeparationModel == SpeechSeparationModel.None && settings.DiarizationEnabled);
            SpeechSeparationEnvironment.Apply(psi.Environment, paths, effectiveSpeechSeparationModel);

            using var process = Process.Start(psi);
            if (process == null)
            {
                OutputBox.Text = L("UnableToStartPython");
                return false;
            }

            var output = await process.StandardOutput.ReadToEndAsync();
            var error = FilterBenignStderr(await process.StandardError.ReadToEndAsync());
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
            {
                OutputBox.Text = $"{L("PythonPackageInstallFailed")}{Environment.NewLine}{output}{Environment.NewLine}{error}";
                return false;
            }

            PackageInstallStamp.MarkCurrent(requirementsPath, stagingDirectory);
            PackageInstallStamp.CommitStagingDirectory(stagingDirectory, targetDirectory);
            return true;
        }
        finally
        {
            PackageInstallStamp.DeleteStagingDirectory(stagingDirectory, targetDirectory);
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        settings.HuggingFaceToken = TokenValue();
        DialogResult = true;
        Close();
    }

    private async Task<bool> CheckAccessAsync()
    {
        settings.HuggingFaceToken = TokenValue();
        if (!RequiresHuggingFaceAccess())
        {
            OutputBox.Text = L("HfAccessNotRequired");
            return true;
        }

        if (string.IsNullOrWhiteSpace(settings.HuggingFaceToken))
        {
            OutputBox.Text = L("SaveTokenBeforeCheck");
            return false;
        }

        try
        {
            OutputBox.Text = L("CheckingHfAccess");
            var pythonExe = await pythonRuntime.EnsureAsync(
                report: (title, detail, _) => OutputBox.Text = $"{title}{Environment.NewLine}{detail}");
            var psi = new ProcessStartInfo
            {
                FileName = pythonExe,
                Arguments = $"\"{paths.WorkerScriptPath}\" --check-hf-access --models \"{paths.ModelDirectory}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            PythonProcessEnvironment.Apply(psi.Environment);
            AsrEngineEnvironment.Apply(psi.Environment, paths, settings.AsrEngine, settings.DiarizationModel);
            psi.Environment["HF_TOKEN"] = settings.HuggingFaceToken;
            psi.Environment["LIVE_DIALOGUE_TRANSLATOR_DIARIZATION_MODEL"] = WorkerProtocol.FormatDiarizationModel(settings.DiarizationModel);

            using var process = Process.Start(psi);
            if (process == null)
            {
                OutputBox.Text = L("UnableToStartPython");
                return false;
            }

            var output = await process.StandardOutput.ReadToEndAsync();
            var error = FilterBenignStderr(await process.StandardError.ReadToEndAsync());
            await process.WaitForExitAsync();
            var details = string.IsNullOrWhiteSpace(error) ? output : output + Environment.NewLine + error;
            OutputBox.Text = process.ExitCode == 0
                ? $"{L("HfAccessOk")}{Environment.NewLine}{details}"
                : $"{L("HfAccessFailed")}{Environment.NewLine}{details}";
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            OutputBox.Text = $"{L("HfAccessFailed")}{Environment.NewLine}{ex.Message}";
            return false;
        }
    }

    private string? TokenValue()
    {
        return string.IsNullOrWhiteSpace(TokenBox.Password) ? null : TokenBox.Password.Trim();
    }

    private bool RequiresHuggingFaceAccess()
    {
        return effectiveSpeechSeparationModel == SpeechSeparationModel.None &&
            settings.DiarizationEnabled &&
            settings.DiarizationModel is not DiarizationModel.Sortformer;
    }

    private static string FilterBenignStderr(string stderr)
    {
        return string.Join(
            Environment.NewLine,
            stderr
                .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                .Where(line => !WorkerStderrClassifier.ShouldIgnore(line)));
    }

    private string L(string key)
    {
        return localizer.Text(key);
    }

    private string LF(string key, params object[] args)
    {
        return localizer.Format(key, args);
    }
}
