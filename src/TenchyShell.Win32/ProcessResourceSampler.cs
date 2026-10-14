using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TenchyShell.Win32;

/// <summary>Reads safe aggregate resource counters for a process that has exited.</summary>
public static class ProcessResourceSampler
{
    public static ProcessResourceSnapshot Capture(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        try
        {
            var readBytes = 0L;
            var writtenBytes = 0L;
            if (NativeMethods.GetProcessIoCounters(process.Handle, out var counters))
            {
                readBytes = SaturatingToInt64(counters.ReadTransferCount);
                writtenBytes = SaturatingToInt64(counters.WriteTransferCount);
            }

            return new ProcessResourceSnapshot(
                Math.Max(0, process.TotalProcessorTime.TotalMilliseconds),
                readBytes,
                writtenBytes,
                Math.Max(0, process.PeakWorkingSet64));
        }
        catch (InvalidOperationException)
        {
            return ProcessResourceSnapshot.Empty;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return ProcessResourceSnapshot.Empty;
        }
    }

    private static long SaturatingToInt64(ulong value) =>
        value > long.MaxValue ? long.MaxValue : (long)value;
}

public readonly record struct ProcessResourceSnapshot(
    double CpuMilliseconds,
    long ReadBytes,
    long WrittenBytes,
    long PeakWorkingSetBytes)
{
    public static ProcessResourceSnapshot Empty => new(0, 0, 0, 0);
}

[StructLayout(LayoutKind.Sequential)]
internal struct IoCounters
{
    internal ulong ReadOperationCount;
    internal ulong WriteOperationCount;
    internal ulong OtherOperationCount;
    internal ulong ReadTransferCount;
    internal ulong WriteTransferCount;
    internal ulong OtherTransferCount;
}
