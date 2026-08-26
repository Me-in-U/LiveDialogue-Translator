using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using LiveDialogueTranslator.Core.Runtime;

namespace LiveDialogueTranslator.App.Services;

public sealed class HardwareDetectionService
{
    public Task<HardwareProfile> DetectAsync(CancellationToken token = default)
    {
        return Task.Run(() => Detect(token), token);
    }

    private static HardwareProfile Detect(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var cpuName = ReadCpuName();
        var memoryBytes = ReadPhysicalMemoryBytes();
        var gpu = DetectNvidiaGpu(token);
        return new HardwareProfile(
            cpuName,
            Math.Max(1, Environment.ProcessorCount),
            memoryBytes,
            gpu.Name,
            gpu.MemoryBytes,
            gpu.DriverAvailable);
    }

    private static string ReadCpuName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return key?.GetValue("ProcessorNameString")?.ToString()?.Trim() is { Length: > 0 } name
                ? name
                : Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "Unknown CPU";
        }
        catch
        {
            return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "Unknown CPU";
        }
    }

    private static long ReadPhysicalMemoryBytes()
    {
        var status = new MemoryStatusEx();
        return GlobalMemoryStatusEx(status)
            ? checked((long)Math.Min(status.TotalPhysical, (ulong)long.MaxValue))
            : 0;
    }

    private static NvidiaGpuInfo DetectNvidiaGpu(CancellationToken token)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=name,memory.total --format=csv,noheader,nounits",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process == null)
            {
                return NvidiaGpuInfo.None;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0)
            {
                return NvidiaGpuInfo.None;
            }

            return output
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(ParseGpuLine)
                .Where(info => info.DriverAvailable)
                .OrderByDescending(info => info.MemoryBytes)
                .FirstOrDefault() ?? NvidiaGpuInfo.None;
        }
        catch
        {
            return NvidiaGpuInfo.None;
        }
    }

    private static NvidiaGpuInfo ParseGpuLine(string line)
    {
        var separator = line.LastIndexOf(',');
        if (separator <= 0)
        {
            return NvidiaGpuInfo.None;
        }

        var name = line[..separator].Trim();
        var memoryText = line[(separator + 1)..].Trim();
        if (name.Length == 0 ||
            !long.TryParse(memoryText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var memoryMiB))
        {
            return NvidiaGpuInfo.None;
        }

        return new NvidiaGpuInfo(name, memoryMiB * 1024L * 1024L, true);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private sealed class MemoryStatusEx
    {
        public uint Length = (uint)Marshal.SizeOf<MemoryStatusEx>();
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    private sealed record NvidiaGpuInfo(string? Name, long MemoryBytes, bool DriverAvailable)
    {
        public static NvidiaGpuInfo None { get; } = new(null, 0, false);
    }
}
