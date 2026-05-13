using System.Runtime.InteropServices;

namespace RemSound.Core;

/// <summary>
/// Boosts the calling thread to MMCSS Pro Audio class plus ThreadPriority.Highest.
/// Dispose on the same thread that constructed it. Designed for capture/render/network audio threads.
/// </summary>
public sealed class WindowsAudioThreadBoost : IDisposable
{
    private readonly IntPtr avrtHandle;
    private readonly ThreadPriority previousPriority;
    private readonly int ownerThreadId;

    public WindowsAudioThreadBoost(string taskName)
    {
        ownerThreadId = Environment.CurrentManagedThreadId;
        previousPriority = Thread.CurrentThread.Priority;
        Thread.CurrentThread.Priority = ThreadPriority.Highest;
        Mode = "ThreadPriority.Highest";

        if (!OperatingSystem.IsWindows()) return;

        avrtHandle = AvSetMmThreadCharacteristics(taskName, out _);
        if (avrtHandle == IntPtr.Zero && !string.Equals(taskName, "Audio", StringComparison.OrdinalIgnoreCase))
        {
            avrtHandle = AvSetMmThreadCharacteristics("Audio", out _);
            if (avrtHandle != IntPtr.Zero) taskName = "Audio";
        }

        if (avrtHandle != IntPtr.Zero)
        {
            AvSetMmThreadPriority(avrtHandle, AvrtPriority.High);
            Mode = $"MMCSS {taskName}";
        }
    }

    public string Mode { get; }

    public void Dispose()
    {
        if (Environment.CurrentManagedThreadId != ownerThreadId) return;
        if (avrtHandle != IntPtr.Zero) AvRevertMmThreadCharacteristics(avrtHandle);
        Thread.CurrentThread.Priority = previousPriority;
    }

    [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "AvSetMmThreadCharacteristicsW")]
    private static extern IntPtr AvSetMmThreadCharacteristics(string taskName, out uint taskIndex);

    [DllImport("avrt.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AvSetMmThreadPriority(IntPtr avrtHandle, AvrtPriority priority);

    [DllImport("avrt.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AvRevertMmThreadCharacteristics(IntPtr avrtHandle);

    private enum AvrtPriority
    {
        Low = -1,
        Normal = 0,
        High = 1,
        Critical = 2,
    }
}
