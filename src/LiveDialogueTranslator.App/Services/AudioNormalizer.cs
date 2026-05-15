using System.IO;
using NAudio.Wave;

namespace LiveDialogueTranslator.App.Services;

public static class AudioNormalizer
{
    private static readonly WaveFormat TargetFormat = new(16000, 16, 1);

    public static byte[] ToPcm16Mono16Khz(WaveFormat inputFormat, byte[] buffer, int bytesRecorded)
    {
        if (bytesRecorded <= 0)
        {
            return [];
        }

        using var input = new RawSourceWaveStream(new MemoryStream(buffer, 0, bytesRecorded), inputFormat);
        using var resampler = new MediaFoundationResampler(input, TargetFormat)
        {
            ResamplerQuality = 30
        };
        using var output = new MemoryStream();
        var temp = new byte[4096];
        int read;
        while ((read = resampler.Read(temp, 0, temp.Length)) > 0)
        {
            output.Write(temp, 0, read);
        }

        return output.ToArray();
    }
}
