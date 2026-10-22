using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace VoiceNative;

// Diagnostic-only socket flows. Closing this qWAVE client releases its requests.
internal sealed class ProbeVoiceQos : IDisposable
{
    private IntPtr handle;
    public List<object> Flows { get; } = [];
    public int? CloseError { get; private set; }

    public static ProbeVoiceQos Create(UdpClient[] clients, IPEndPoint[] endpoints)
    {
        var result = new ProbeVoiceQos();
        var version = new QosVersion { Major = 1, Minor = 0 };
        if (!QOSCreateHandle(ref version, out result.handle))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Diagnostic qWAVE creation failed.");
        try
        {
            for (var i = 1; i < clients.Length; i++)
            {
                var address = endpoints[i].Serialize();
                var bytes = new byte[address.Size];
                for (var n = 0; n < bytes.Length; n++) bytes[n] = address[n];
                uint id = 0;
                if (!QOSAddSocketToFlow(result.handle, clients[i].Client.Handle, bytes, 4 /* Voice */, 2 /* NON_ADAPTIVE_FLOW */, ref id))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Diagnostic qWAVE flow failed.");
                result.Flows.Add(new { streamIndex = i, flowId = id, trafficType = "Voice", nonAdaptive = true });
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    public void Dispose()
    {
        if (handle == IntPtr.Zero) return;
        CloseError = QOSCloseHandle(handle) ? 0 : Marshal.GetLastWin32Error();
        handle = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct QosVersion { public ushort Major, Minor; }
    [DllImport("qwave.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QOSCreateHandle(ref QosVersion version, out IntPtr handle);
    [DllImport("qwave.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QOSAddSocketToFlow(IntPtr handle, IntPtr socket, [In] byte[] address, int trafficType, uint flags, ref uint flowId);
    [DllImport("qwave.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QOSCloseHandle(IntPtr handle);
}
