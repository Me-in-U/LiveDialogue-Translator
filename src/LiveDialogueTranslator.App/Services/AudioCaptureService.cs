using NAudio.Wave;
using NAudio.CoreAudioApi;

namespace LiveDialogueTranslator.App.Services;

public sealed class AudioChunkEventArgs : EventArgs
{
    public AudioChunkEventArgs(string source, long timestampMs, byte[] pcm16Mono16Khz)
    {
        Source = source;
        TimestampMs = timestampMs;
        Pcm16Mono16Khz = pcm16Mono16Khz;
    }

    public string Source { get; }
    public long TimestampMs { get; }
    public byte[] Pcm16Mono16Khz { get; }
}

public sealed class AudioCaptureService : IDisposable
{
    private readonly object gate = new();
    private WasapiLoopbackCapture? systemCapture;
    private WasapiCapture? micCapture;
    private DateTimeOffset startedAt;

    public event EventHandler<AudioChunkEventArgs>? ChunkCaptured;
    public event EventHandler<string>? CaptureError;

    public bool IsRunning { get; private set; }

    public void Start(bool includeSystemAudio, bool includeMicrophone)
    {
        lock (gate)
        {
            if (IsRunning)
            {
                return;
            }

            startedAt = DateTimeOffset.UtcNow;

            try
            {
                if (includeSystemAudio)
                {
                    systemCapture = new WasapiLoopbackCapture();
                    systemCapture.DataAvailable += (_, args) => PublishChunk("system", systemCapture.WaveFormat, args.Buffer, args.BytesRecorded);
                    systemCapture.RecordingStopped += (_, args) => ReportStoppedError(args.Exception);
                    systemCapture.StartRecording();
                }

                if (includeMicrophone)
                {
                    micCapture = new WasapiCapture();
                    micCapture.DataAvailable += (_, args) => PublishChunk("mic", micCapture.WaveFormat, args.Buffer, args.BytesRecorded);
                    micCapture.RecordingStopped += (_, args) => ReportStoppedError(args.Exception);
                    micCapture.StartRecording();
                }

                IsRunning = true;
            }
            catch (Exception ex)
            {
                Stop();
                CaptureError?.Invoke(this, ex.Message);
            }
        }
    }

    public void Stop()
    {
        lock (gate)
        {
            systemCapture?.StopRecording();
            micCapture?.StopRecording();
            systemCapture?.Dispose();
            micCapture?.Dispose();
            systemCapture = null;
            micCapture = null;
            IsRunning = false;
        }
    }

    public void Dispose()
    {
        Stop();
    }

    private void PublishChunk(string source, WaveFormat inputFormat, byte[] buffer, int bytesRecorded)
    {
        try
        {
            var normalized = AudioNormalizer.ToPcm16Mono16Khz(inputFormat, buffer, bytesRecorded);
            if (normalized.Length == 0)
            {
                return;
            }

            var timestampMs = (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
            ChunkCaptured?.Invoke(this, new AudioChunkEventArgs(source, timestampMs, normalized));
        }
        catch (Exception ex)
        {
            CaptureError?.Invoke(this, ex.Message);
        }
    }

    private void ReportStoppedError(Exception? exception)
    {
        if (exception != null)
        {
            CaptureError?.Invoke(this, exception.Message);
        }
    }
}
