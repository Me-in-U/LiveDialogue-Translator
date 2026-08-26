using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using LiveDialogueTranslator.App.Models;
using LiveDialogueTranslator.Core.Protocol;
using LiveDialogueTranslator.Core.Runtime;
using LiveDialogueTranslator.Core.Startup;

namespace LiveDialogueTranslator.App.Services;

public sealed record WorkerSetupProgress(string Title, string Detail, double? Percent);

public sealed class WorkerEnvironmentService
{
    private static readonly HttpClient HuggingFaceHttpClient = new();
    private readonly AppPaths paths;
    private readonly Localizer localizer;
    private readonly PythonRuntimeService pythonRuntime;

    public WorkerEnvironmentService(AppPaths paths, Localizer localizer)
    {
        this.paths = paths;
        this.localizer = localizer;
        pythonRuntime = new PythonRuntimeService(paths, localizer);
    }

    public event EventHandler<WorkerSetupProgress>? ProgressChanged;
    public event EventHandler<WorkerLogLine>? LogReceived;

    public async Task<WorkerStartupPlan> EnsureReadyAsync(
        AppSettings settings,
        SpeechSeparationModel effectiveSpeechSeparationModel,
        CancellationToken token = default)
    {
        Report(L("CheckingLocalSpeechSetup"), L("CheckingPythonPackagesModels"), 0.05);
        await pythonRuntime.EnsureAsync(token, Report);
        var state = await InspectAsync(settings, effectiveSpeechSeparationModel, token);
        var plan = WorkerStartupPlanner.CreatePlan(state);

        // Fail before package/CUDA setup when gated model files must be
        // downloaded and the saved token cannot access the required repos.
        if (RequiresHuggingFaceAccessBeforeSetup(settings, effectiveSpeechSeparationModel, plan))
        {
            var earlyAccessError = await CheckHuggingFaceAccessAsync(settings, token);
            if (earlyAccessError != null)
            {
                Report(L("SpeechSetupWarning"), L("LocalDiarizationNeedsAccess"), 0.95);
                return new WorkerStartupPlan(
                    [],
                    StartupCapability.NeedsHuggingFaceAccess,
                    earlyAccessError);
            }
        }

        foreach (var action in plan.Actions)
        {
            switch (action.Kind)
            {
                case StartupActionKind.InstallPythonPackages:
                    await InstallPackagesAsync(token);
                    await InstallOptionalDiarizationPackagesAsync(settings, effectiveSpeechSeparationModel, token);
                    await InstallAsrEnginePackagesAsync(settings, effectiveSpeechSeparationModel, token);
                    await InstallSpeechSeparationPackagesAsync(effectiveSpeechSeparationModel, settings, token);
                    break;
            }
        }

        await EnsureCudaAccelerationAsync(settings, token);

        foreach (var action in plan.Actions)
        {
            switch (action.Kind)
            {
                case StartupActionKind.PrepareModels:
                    await PrepareModelsAsync(settings, effectiveSpeechSeparationModel, token);
                    break;
            }
        }

        if (plan.Warning != null)
        {
            var warning = plan.Capability == StartupCapability.NeedsHuggingFaceAccess
                ? L("LocalDiarizationNeedsAccess")
                : plan.Warning;
            Report(L("SpeechSetupWarning"), warning, 0.95);
        }

        Report(L("SpeechSetupReady"), DescribeCapability(plan.Capability), 1);
        return plan;
    }

    public async Task RepairAsync(
        AppSettings settings,
        SpeechSeparationModel effectiveSpeechSeparationModel,
        CancellationToken token = default)
    {
        Report(L("InstallingLocalSpeechPackages"), L("ReinstallingWorkerRequirements"), 0.1);
        await pythonRuntime.EnsureAsync(token, Report);
        await InstallPackagesAsync(token);
        await InstallOptionalDiarizationPackagesAsync(settings, effectiveSpeechSeparationModel, token);
        await InstallAsrEnginePackagesAsync(settings, effectiveSpeechSeparationModel, token);
        await InstallSpeechSeparationPackagesAsync(effectiveSpeechSeparationModel, settings, token);
        await EnsureCudaAccelerationAsync(settings, token);
        Report(L("PreparingLocalSpeechModels"), L("DownloadingOrValidatingModels"), 0.7);
        await PrepareModelsAsync(settings, effectiveSpeechSeparationModel, token);
        Report(L("SpeechSetupReady"), L("WorkerRequirementsReady"), 1);
    }

    private async Task<WorkerStartupState> InspectAsync(
        AppSettings settings,
        SpeechSeparationModel effectiveSpeechSeparationModel,
        CancellationToken token)
    {
        if (!File.Exists(paths.WorkerScriptPath))
        {
            Report(L("WorkerMissingTitle"), paths.WorkerScriptPath, 1);
            return UnavailableState(settings);
        }

        try
        {
            var result = await RunProcessAsync(
                paths.PythonExecutablePath,
                $"\"{paths.WorkerScriptPath}\" --check --models \"{paths.ModelDirectory}\"",
                settings,
                token,
                progressTitle: L("CheckingPythonWorker"),
                speechSeparationModel: effectiveSpeechSeparationModel);

            if (result.ExitCode != 0)
            {
                return UnavailableState(settings);
            }

            var jsonLine = result.StdOut
                .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault(line => line.TrimStart().StartsWith("{", StringComparison.Ordinal));
            if (jsonLine == null)
            {
                return UnavailableState(settings) with
                {
                    PythonAvailable = true
                };
            }

            using var document = JsonDocument.Parse(jsonLine);
            var root = document.RootElement;
            return new WorkerStartupState(
                PythonAvailable: true,
                LocalWhisperRequested: true,
                FasterWhisperAvailable: root.GetProperty("fasterWhisperAvailable").GetBoolean(),
                PyannoteAvailable: root.GetProperty("pyannoteAvailable").GetBoolean(),
                DiartAvailable: root.TryGetProperty("diartAvailable", out var diartAvailable) && diartAvailable.GetBoolean(),
                TorchAvailable: root.GetProperty("torchAvailable").GetBoolean(),
                SttModelPrepared: root.GetProperty("sttModelPrepared").GetBoolean(),
                SttModelLoadable: root.TryGetProperty("sttModelLoadable", out var sttLoadable) && sttLoadable.GetBoolean(),
                DiarizationModelPrepared: root.TryGetProperty("diarizationModelPrepared", out var diarizationPrepared)
                    ? diarizationPrepared.GetBoolean()
                    : !settings.DiarizationEnabled || settings.DiarizationModel == DiarizationModel.Sortformer,
                DiarizationRequested: settings.DiarizationEnabled && effectiveSpeechSeparationModel == SpeechSeparationModel.None,
                DiarizationModel: settings.DiarizationModel,
                AsrEngine: settings.AsrEngine,
                QwenAsrAvailable: root.TryGetProperty("qwenAsrAvailable", out var qwenAvailable) && qwenAvailable.GetBoolean(),
                WhisperLiveKitAvailable: root.TryGetProperty("whisperLiveKitAvailable", out var whisperLiveKitAvailable) && whisperLiveKitAvailable.GetBoolean(),
                WhisperXAvailable: root.TryGetProperty("whisperXAvailable", out var whisperXAvailable) && whisperXAvailable.GetBoolean(),
                HasHuggingFaceToken: HasToken(settings),
                SpeechSeparationModel: effectiveSpeechSeparationModel,
                SpeechSeparationPackageAvailable: root.TryGetProperty("speechSeparationAvailable", out var separationAvailable) && separationAvailable.GetBoolean(),
                SpeechSeparationModelPrepared: root.TryGetProperty("speechSeparationModelPrepared", out var separationPrepared) && separationPrepared.GetBoolean());
        }
        catch (Exception ex)
        {
            Report(L("PythonCheckFailed"), ex.Message, 1);
            return UnavailableState(settings);
        }
    }

    private async Task InstallPackagesAsync(CancellationToken token)
    {
        var requirementsPath = Path.Combine(AppContext.BaseDirectory, "worker", "requirements.txt");
        if (!File.Exists(requirementsPath))
        {
            requirementsPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "worker", "requirements.txt"));
        }

        Report(L("InstallingLocalSpeechPackages"), L("FirstInstallCanTakeMinutes"), 0.2);
        var result = await RunProcessAsync(
            paths.PythonExecutablePath,
            PythonPipCommands.InstallRequirementsArguments(requirementsPath),
            null,
            token,
            progressTitle: L("InstallingPackages"));

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"{L("PythonPackageInstallFailed")}{Environment.NewLine}{result.StdErr}{Environment.NewLine}{result.StdOut}");
        }
    }

    private async Task InstallOptionalDiarizationPackagesAsync(
        AppSettings settings,
        SpeechSeparationModel speechSeparationModel,
        CancellationToken token)
    {
        if (speechSeparationModel != SpeechSeparationModel.None || settings.DiarizationModel != DiarizationModel.Diart)
        {
            return;
        }

        Report(L("InstallingLocalSpeechPackages"), L("InstallingDiartPackage"), 0.32);
        var result = await RunProcessAsync(
            paths.PythonExecutablePath,
            PythonPipCommands.InstallDiartArguments(),
            null,
            token,
            progressTitle: L("InstallingPackages"));

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"{L("PythonPackageInstallFailed")}{Environment.NewLine}{result.StdErr}{Environment.NewLine}{result.StdOut}");
        }
    }

    private async Task InstallAsrEnginePackagesAsync(
        AppSettings settings,
        SpeechSeparationModel speechSeparationModel,
        CancellationToken token)
    {
        var engines = AsrEngineEnvironment.RequiredAsrEngines(
            settings.AsrEngine,
            settings.DiarizationModel,
            settings.DiarizationEnabled && speechSeparationModel == SpeechSeparationModel.None);
        if (engines.Contains(AsrEngine.WhisperLiveKitSortformer))
        {
            Report(L("InstallingAsrEnginePackages"), L("AsrEnginePackagesCanTakeMinutes"), 0.34);
        }

        if (engines.Count == 0)
        {
            return;
        }

        foreach (var engine in engines)
        {
            var requirementsPath = AsrEngineEnvironment.RequirementsPath(engine);
            if (!File.Exists(requirementsPath))
            {
                throw new InvalidOperationException($"ASR engine requirements file not found: {requirementsPath}");
            }

            var targetDirectory = paths.AsrPackageDirectory(engine);
            var stagingDirectory = PackageInstallStamp.CreateStagingDirectory(targetDirectory);
            try
            {
                Report(L("InstallingAsrEnginePackages"), L("AsrEnginePackagesCanTakeMinutes"), 0.36);
                var result = await RunProcessAsync(
                    paths.PythonExecutablePath,
                    PythonPipCommands.InstallRequirementsToTargetArguments(
                        requirementsPath,
                        stagingDirectory,
                        includeCudaTorchIndex: engine == AsrEngine.WhisperX),
                    settings,
                    token,
                    progressTitle: L("InstallingAsrEnginePackages"));

                if (result.ExitCode != 0)
                {
                    throw new InvalidOperationException($"{L("PythonPackageInstallFailed")}{Environment.NewLine}{result.StdErr}{Environment.NewLine}{result.StdOut}");
                }

                PackageInstallStamp.MarkCurrent(requirementsPath, stagingDirectory);
                PackageInstallStamp.CommitStagingDirectory(stagingDirectory, targetDirectory);
            }
            finally
            {
                PackageInstallStamp.DeleteStagingDirectory(stagingDirectory, targetDirectory);
            }
        }
    }

    private async Task InstallSpeechSeparationPackagesAsync(
        SpeechSeparationModel model,
        AppSettings settings,
        CancellationToken token)
    {
        if (model is SpeechSeparationModel.None or SpeechSeparationModel.Auto)
        {
            return;
        }

        var requirementsPath = SpeechSeparationEnvironment.RequirementsPath(model);
        if (!File.Exists(requirementsPath))
        {
            throw new InvalidOperationException($"Speech separation requirements file not found: {requirementsPath}");
        }

        var targetDirectory = paths.SpeechSeparationPackageDirectory(model);
        var stagingDirectory = PackageInstallStamp.CreateStagingDirectory(targetDirectory);
        try
        {
            Report(L("InstallingSpeechSeparationPackages"), L("SpeechSeparationPackagesCanTakeMinutes"), 0.4);
            var result = await RunProcessAsync(
                paths.PythonExecutablePath,
                PythonPipCommands.InstallRequirementsToTargetArguments(requirementsPath, stagingDirectory),
                settings,
                token,
                progressTitle: L("InstallingSpeechSeparationPackages"),
                speechSeparationModel: model);

            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException($"{L("PythonPackageInstallFailed")}{Environment.NewLine}{result.StdErr}{Environment.NewLine}{result.StdOut}");
            }

            PackageInstallStamp.MarkCurrent(requirementsPath, stagingDirectory);
            PackageInstallStamp.CommitStagingDirectory(stagingDirectory, targetDirectory);
        }
        finally
        {
            PackageInstallStamp.DeleteStagingDirectory(stagingDirectory, targetDirectory);
        }
    }

    private async Task EnsureCudaAccelerationAsync(AppSettings settings, CancellationToken token)
    {
        if (settings.ComputeMode == ComputeMode.Cpu)
        {
            return;
        }

        if (!await NvidiaGpuAvailableAsync(token))
        {
            return;
        }

        if (await TorchCudaAvailableAsync(token))
        {
            return;
        }

        Report(L("InstallingCudaAcceleration"), L("CudaInstallCanTakeMinutes"), 0.35);
        var result = await RunProcessAsync(
            paths.PythonExecutablePath,
            PythonPipCommands.InstallCudaTorchArguments(),
            null,
            token,
            progressTitle: L("InstallingCudaAcceleration"));

        if (result.ExitCode == 0)
        {
            Report(L("InstallingCudaAcceleration"), L("CudaAccelerationReady"), 0.45);
            return;
        }

        var message = $"{L("CudaAccelerationInstallFailed")}{Environment.NewLine}{result.StdErr}{Environment.NewLine}{result.StdOut}";
        if (settings.ComputeMode == ComputeMode.Cuda)
        {
            throw new InvalidOperationException(message);
        }

        Report(L("SpeechSetupWarning"), message, 0.45);
    }

    private async Task<bool> NvidiaGpuAvailableAsync(CancellationToken token)
    {
        try
        {
            var result = await RunProcessAsync(
                "nvidia-smi",
                "-L",
                null,
                token,
                progressTitle: L("CheckingGpuAcceleration"));
            return result.ExitCode == 0 && result.StdOut.Contains("GPU", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> TorchCudaAvailableAsync(CancellationToken token)
    {
        try
        {
            var result = await RunProcessAsync(
                paths.PythonExecutablePath,
                "-c \"import torch; raise SystemExit(0 if torch.cuda.is_available() else 1)\"",
                null,
                token,
                progressTitle: L("CheckingGpuAcceleration"));
            return result.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private async Task PrepareModelsAsync(
        AppSettings settings,
        SpeechSeparationModel speechSeparationModel,
        CancellationToken token)
    {
        Report(L("PreparingLocalSpeechModels"), L("DownloadingOrValidatingModels"), 0.65);
        var result = await RunProcessAsync(
            paths.PythonExecutablePath,
            $"\"{paths.WorkerScriptPath}\" --download --models \"{paths.ModelDirectory}\"",
            settings,
            token,
            progressTitle: L("PreparingModels"),
            speechSeparationModel: speechSeparationModel);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"{L("ModelPreparationFailed")}{Environment.NewLine}{result.StdErr}{Environment.NewLine}{result.StdOut}");
        }
    }

    private async Task<string?> CheckHuggingFaceAccessAsync(AppSettings settings, CancellationToken token)
    {
        if (!DiarizationRequiresHuggingFaceAccess(settings))
        {
            return null;
        }

        if (!HasToken(settings))
        {
            return $"{L("HfAccessFailed")}{Environment.NewLine}{L("SaveTokenBeforeCheck")}";
        }

        Report(L("CheckingHfAccess"), L("LocalDiarizationNeedsAccess"), 0.55);
        foreach (var modelId in RequiredHuggingFaceModelIds(settings.DiarizationModel))
        {
            var accessError = await CheckHuggingFaceModelAccessAsync(modelId, settings.HuggingFaceToken!, token);
            if (accessError != null)
            {
                return accessError;
            }
        }

        return null;
    }

    private async Task<ProcessResult> RunProcessAsync(
        string fileName,
        string arguments,
        AppSettings? settings,
        CancellationToken token,
        string progressTitle,
        SpeechSeparationModel? speechSeparationModel = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        PythonProcessEnvironment.Apply(psi.Environment);
        if (!string.IsNullOrWhiteSpace(settings?.HuggingFaceToken))
        {
            psi.Environment["HF_TOKEN"] = settings.HuggingFaceToken;
        }
        if (!string.IsNullOrWhiteSpace(settings?.SttModel))
        {
            psi.Environment["LIVE_DIALOGUE_TRANSLATOR_STT_MODEL"] = settings.SttModel;
        }
        if (settings != null)
        {
            var effectiveDiarizationEnabled = settings.DiarizationEnabled &&
                speechSeparationModel is null or SpeechSeparationModel.None;
            AsrEngineEnvironment.Apply(
                psi.Environment,
                paths,
                settings.AsrEngine,
                settings.DiarizationModel,
                effectiveDiarizationEnabled);
            SpeechSeparationEnvironment.Apply(
                psi.Environment,
                paths,
                speechSeparationModel ?? settings.SpeechSeparationModel);
            psi.Environment["LIVE_DIALOGUE_TRANSLATOR_DIARIZATION_ENABLED"] =
                effectiveDiarizationEnabled
                    ? "true"
                    : "false";
            psi.Environment["LIVE_DIALOGUE_TRANSLATOR_DIARIZATION_MODEL"] = WorkerProtocol.FormatDiarizationModel(settings.DiarizationModel);
            psi.Environment["LIVE_DIALOGUE_TRANSLATOR_ASR_ENGINE"] = WorkerProtocol.FormatAsrEngine(settings.AsrEngine);
            psi.Environment["LIVE_DIALOGUE_TRANSLATOR_STT_QUALITY_PRESET"] = settings.SttQualityPreset.ToString();
            psi.Environment["LIVE_DIALOGUE_TRANSLATOR_DIARIZATION_QUALITY_PRESET"] = settings.DiarizationQualityPreset.ToString();
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Unable to start {fileName}.");
        var stdout = new List<string>();
        var stderr = new List<string>();

        var stdoutTask = ReadLinesAsync(process.StandardOutput, stdout, "stdout", progressTitle, token);
        var stderrTask = ReadLinesAsync(process.StandardError, stderr, "stderr", progressTitle, token);

        await process.WaitForExitAsync(token);
        await Task.WhenAll(stdoutTask, stderrTask);
        return new ProcessResult(process.ExitCode, string.Join(Environment.NewLine, stdout), string.Join(Environment.NewLine, stderr));
    }

    private async Task ReadLinesAsync(StreamReader reader, List<string> lines, string stream, string progressTitle, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(token);
            if (line == null)
            {
                break;
            }

            LogReceived?.Invoke(this, new WorkerLogLine(stream, line));

            if (WorkerStderrClassifier.ShouldIgnore(line))
            {
                continue;
            }

            lines.Add(line);
            Report(progressTitle, TrimLine(line), null);
        }
    }

    private void Report(string title, string detail, double? percent)
    {
        ProgressChanged?.Invoke(this, new WorkerSetupProgress(title, detail, percent));
    }

    private static bool HasToken(AppSettings settings)
    {
        return !string.IsNullOrWhiteSpace(settings.HuggingFaceToken);
    }

    private static bool RequiresHuggingFaceAccessBeforeSetup(
        AppSettings settings,
        SpeechSeparationModel speechSeparationModel,
        WorkerStartupPlan plan)
    {
        // A missing or invalid token should not block already-cached local
        // models; it only matters before work that may reach Hugging Face.
        return speechSeparationModel == SpeechSeparationModel.None &&
            DiarizationRequiresHuggingFaceAccess(settings) &&
            (plan.Capability == StartupCapability.NeedsHuggingFaceAccess || NeedsModelPreparation(plan));
    }

    private static bool NeedsModelPreparation(WorkerStartupPlan plan)
    {
        return plan.Actions.Any(action => action.Kind == StartupActionKind.PrepareModels);
    }

    private static bool DiarizationRequiresHuggingFaceAccess(AppSettings settings)
    {
        return settings.DiarizationEnabled && settings.DiarizationModel != DiarizationModel.Sortformer;
    }

    private static IReadOnlyList<string> RequiredHuggingFaceModelIds(DiarizationModel model)
    {
        return model == DiarizationModel.Diart
            ? [HuggingFaceLinks.DiartSegmentationModelId, HuggingFaceLinks.DiartEmbeddingModelId]
            : [HuggingFaceLinks.CommunityModelId];
    }

    private async Task<string?> CheckHuggingFaceModelAccessAsync(string modelId, string tokenValue, CancellationToken token)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://huggingface.co/{modelId}/resolve/main/config.yaml");
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {tokenValue.Trim()}");
            using var response = await HuggingFaceHttpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                token);
            if (response.IsSuccessStatusCode)
            {
                return null;
            }

            return $"{L("HfAccessFailed")}{Environment.NewLine}{modelId}: {(int)response.StatusCode} {response.ReasonPhrase}".Trim();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"{L("HfAccessFailed")}{Environment.NewLine}{modelId}: {ex.Message}".Trim();
        }
    }

    private static WorkerStartupState UnavailableState(AppSettings settings)
    {
        return new WorkerStartupState(
            PythonAvailable: false,
            LocalWhisperRequested: true,
            FasterWhisperAvailable: false,
            PyannoteAvailable: false,
            DiartAvailable: false,
            TorchAvailable: false,
            SttModelPrepared: false,
            SttModelLoadable: false,
            DiarizationModelPrepared: false,
            DiarizationRequested: settings.DiarizationEnabled,
            DiarizationModel: settings.DiarizationModel,
            AsrEngine: settings.AsrEngine,
            QwenAsrAvailable: false,
            WhisperLiveKitAvailable: false,
            WhisperXAvailable: false,
            HasHuggingFaceToken: HasToken(settings),
            SpeechSeparationModel: SpeechSeparationModel.None,
            SpeechSeparationPackageAvailable: false,
            SpeechSeparationModelPrepared: false);
    }

    private string DescribeCapability(StartupCapability capability)
    {
        return capability switch
        {
            StartupCapability.FullDiarization => L("SttDiarizationReady"),
            StartupCapability.SpeechSeparation => L("SpeechSeparationReady"),
            StartupCapability.NeedsHuggingFaceAccess => L("LocalDiarizationNeedsAccess"),
            StartupCapability.SttOnly => L("SttOnlyReady"),
            _ => L("LocalSpeechUnavailable")
        };
    }

    private string L(string key)
    {
        return localizer.Text(key);
    }

    private static string TrimLine(string line)
    {
        return line.Length > 180 ? line[..180] + "..." : line;
    }

    private sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);
}
