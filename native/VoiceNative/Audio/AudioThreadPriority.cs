using System.Runtime.InteropServices;

namespace VoiceNative;

// Thread-local registration only; never edits machine MMCSS policy.
internal sealed class AudioThreadPriority : IDisposable
{
    private readonly IntPtr handle;
    public string TaskName { get; }
    public bool Active => handle != IntPtr.Zero;
    public bool? CriticalSet { get; }
    public int? Error { get; }
    private bool disposed;
    public AudioThreadPriority(string taskName, bool critical)
    {
        TaskName = taskName;
        uint index = 0;
        handle = AvSetMmThreadCharacteristics(taskName, ref index);
        if (handle == IntPtr.Zero) { Error = Marshal.GetLastWin32Error(); return; }
        if (critical)
        {
            CriticalSet = AvSetMmThreadPriority(handle, 2); // AVRT_PRIORITY_CRITICAL
            if (CriticalSet == false) Error = Marshal.GetLastWin32Error();
        }
    }
    public void Dispose()
    {
        if (disposed) return;
        if (handle != IntPtr.Zero) AvRevertMmThreadCharacteristics(handle);
        disposed = true;
    }
    [DllImport("avrt.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true, EntryPoint = "AvSetMmThreadCharacteristicsW")]
    private static extern IntPtr AvSetMmThreadCharacteristics(string taskName, ref uint taskIndex);
    [DllImport("avrt.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AvSetMmThreadPriority(IntPtr handle, int priority);
    [DllImport("avrt.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);
}
