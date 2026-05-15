using System.Diagnostics;
using System.IO;
using System.Text;
using LiveDialogueTranslator.Core.Protocol;
using LiveDialogueTranslator.Core.Runtime;

namespace LiveDialogueTranslator.App.Services;

public sealed record WorkerLogLine(string Stream, string Message);

public sealed class WorkerClient : IDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly TimeSpan WorkerStartTimeout = TimeSpan.FromMinutes(10);
    private readonly AppPaths paths;
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private Process? process;
    private CancellationTokenSource? readCts;
    private TaskCompletionSource? pendingStart;

    public WorkerClient(AppPaths paths)
    {
        this.paths = paths;
    }

    public event EventHandler<IWorkerEvent>? EventReceived;
    public event EventHandler<WorkerLogLine>? LogReceived;
    public bool IsRunning => process is { HasExited: false };

    public async Task StartAsync(WorkerConfiguration configuration, string? huggingFaceToken = null, CancellationToken token = default)
    {
        if (IsRunning)
        {
            await StopAsync(token);
        }

        if (!File.Exists(paths.WorkerScriptPath))
        {
            EventReceived?.Invoke(this, new WorkerErrorEvent("worker_missing", $"Worker script not found: {paths.WorkerScriptPath}", true));
            return;
        }

        var psi = new ProcessStartInfo
        {
            FileName = ResolvePythonCommand(),
            Arguments = $"\"{paths.WorkerScriptPath}\" --stdio --models \"{paths.ModelDirectory}\"",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = Utf8NoBom,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom
        };
        PythonProcessEnvironment.Apply(psi.Environment);
        AsrEngineEnvironment.Apply(psi.Environment, paths, configuration.AsrEngine, configuration.DiarizationModel);
        if (!string.IsNullOrWhiteSpace(huggingFaceToken))
        {
            psi.Environment["HF_TOKEN"] = huggingFaceToken;
            psi.Environment["HUGGINGFACE_TOKEN"] = huggingFaceToken;
        }

        process = Process.Start(psi);
        if (process == null)
        {
            EventReceived?.Invoke(this, new WorkerErrorEvent("worker_start_failed", "Unable to start Python worker.", true));
            return;
        }

        readCts = new CancellationTokenSource();
        _ = Task.Run(() => ReadLoopAsync(process, readCts.Token), CancellationToken.None);
        _ = Task.Run(() => ErrorLoopAsync(process, readCts.Token), CancellationToken.None);

        pendingStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await SendAsync(WorkerProtocol.Configure(configuration), token);
        await SendAsync(WorkerProtocol.Start(), token);
        await WaitForWorkerListeningAsync(token);
    }

    public async Task StopAsync(CancellationToken token = default)
    {
        if (!IsRunning)
        {
            return;
        }

        await SendAsync(WorkerProtocol.Stop(), token);
        pendingStart?.TrySetCanceled(token);
        pendingStart = null;
        readCts?.Cancel();
        if (process is { HasExited: false })
        {
            process.Kill(entireProcessTree: true);
        }

        process?.Dispose();
        process = null;
    }

    public Task SendAudioAsync(string source, long timestampMs, byte[] pcm16Mono16Khz, CancellationToken token = default)
    {
        return IsRunning
            ? SendAsync(WorkerProtocol.AudioChunk(source, timestampMs, pcm16Mono16Khz), token)
            : Task.CompletedTask;
    }

    public void Dispose()
    {
        readCts?.Cancel();
        pendingStart?.TrySetCanceled();
        if (process is { HasExited: false })
        {
            process.Kill(entireProcessTree: true);
        }

        process?.Dispose();
        writeLock.Dispose();
        readCts?.Dispose();
    }

    private async Task SendAsync(WorkerCommand command, CancellationToken token)
    {
        if (process == null || process.HasExited)
        {
            return;
        }

        var json = WorkerProtocol.Serialize(command);
        await writeLock.WaitAsync(token);
        try
        {
            await process.StandardInput.WriteAsync(json.AsMemory(), token);
            await process.StandardInput.FlushAsync();
        }
        finally
        {
            writeLock.Release();
        }
    }

    private async Task ReadLoopAsync(Process runningProcess, CancellationToken token)
    {
        while (!token.IsCancellationRequested && !runningProcess.HasExited)
        {
            var line = await runningProcess.StandardOutput.ReadLineAsync(token);
            if (line == null)
            {
                break;
            }

            LogReceived?.Invoke(this, new WorkerLogLine("stdout", line));

            try
            {
                var workerEvent = WorkerProtocol.ParseEvent(line);
                CompletePendingStart(workerEvent);
                EventReceived?.Invoke(this, workerEvent);
            }
            catch (Exception ex)
            {
                EventReceived?.Invoke(this, new WorkerErrorEvent("worker_protocol_error", ex.Message, true));
            }
        }
    }

    private async Task ErrorLoopAsync(Process runningProcess, CancellationToken token)
    {
        while (!token.IsCancellationRequested && !runningProcess.HasExited)
        {
            var line = await runningProcess.StandardError.ReadLineAsync(token);
            if (line == null)
            {
                break;
            }

            var message = line ?? "";
            if (WorkerStderrClassifier.ShouldIgnore(message))
            {
                continue;
            }

            LogReceived?.Invoke(this, new WorkerLogLine("stderr", message));
            EventReceived?.Invoke(this, new WorkerErrorEvent("worker_stderr", message, true));
        }
    }

    private Task WaitForWorkerListeningAsync(CancellationToken token)
    {
        var startTask = pendingStart?.Task ?? Task.CompletedTask;
        return WaitForWorkerListeningCoreAsync(startTask, token);
    }

    private async Task WaitForWorkerListeningCoreAsync(Task startTask, CancellationToken token)
    {
        try
        {
            await startTask.WaitAsync(WorkerStartTimeout, token);
        }
        finally
        {
            if (pendingStart?.Task == startTask)
            {
                pendingStart = null;
            }
        }
    }

    private void CompletePendingStart(IWorkerEvent workerEvent)
    {
        if (pendingStart == null)
        {
            return;
        }

        if (workerEvent is ModelStatusEvent status)
        {
            if (status.Stage.Equals("listening", StringComparison.OrdinalIgnoreCase))
            {
                pendingStart.TrySetResult();
            }
            else if (status.Stage.Equals("setup_failed", StringComparison.OrdinalIgnoreCase))
            {
                pendingStart.TrySetException(new InvalidOperationException(status.Message));
            }
        }
        else if (workerEvent is WorkerErrorEvent error &&
                 (error.Code.Equals("worker_missing", StringComparison.OrdinalIgnoreCase) ||
                  error.Code.Equals("worker_start_failed", StringComparison.OrdinalIgnoreCase)))
        {
            pendingStart.TrySetException(new InvalidOperationException(error.Message));
        }
    }

    private string ResolvePythonCommand()
    {
        return paths.PythonExecutablePath;
    }
}
