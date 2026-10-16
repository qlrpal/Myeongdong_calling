using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace VoiceNative;

// Read-only native WLAN queries; no scan, current_connection, SSID/BSSID, profiles, keys or setters.
internal static class WlanMetricsProbe
{
    private static readonly string[] PhyNames = ["tx", "multicastTx", "failed", "retry", "multipleRetry",
        "txLifetimeExceeded", "txFragments", "rtsSuccess", "rtsFailure", "ackFailure", "rx", "multicastRx",
        "promiscuousRx", "rxLifetimeExceeded", "duplicates", "rxFragments", "promiscuousRxFragments", "fcsError"];
    private sealed record ApiResult(uint Code, uint Size, double StartedAt, double EndedAt, object? Data, string? DecodeError);

    public static async Task RunAsync(int seconds, string path)
    {
        if (seconds is < 1 or > 180) throw new ArgumentOutOfRangeException(nameof(seconds));
        var snapshots = new List<object>(); var open = WlanOpenHandle(2, IntPtr.Zero, out var version, out var handle);
        uint? closeCode = null;
        try
        {
            if (open == 0)
            {
                var watch = Stopwatch.StartNew();
                do
                {
                    var started = ProbeClock.Now; var interfaces = new List<object>();
                    var enumerate = WlanEnumInterfaces(handle, IntPtr.Zero, out var list);
                    try
                    {
                        if (enumerate == 0)
                        {
                            if (list == IntPtr.Zero) throw new InvalidDataException("Missing WLAN interface buffer.");
                            var count = Marshal.ReadInt32(list);
                            if (count is < 0 or > 64) throw new InvalidDataException("Unexpected WLAN interface count.");
                            for (var i = 0; i < count; i++)
                            {
                                // WLAN_INTERFACE_INFO: GUID16 + WCHAR[256]512 + state4 = 532.
                                var entry = IntPtr.Add(list, 8 + i * 532);
                                var id = Marshal.PtrToStructure<Guid>(entry); var state = Marshal.ReadInt32(entry, 528);
                                if (state != 1) { interfaces.Add(new { id, state }); continue; }
                                interfaces.Add(new
                                {
                                    id, state,
                                    statistics = Query(handle, id, 0x10000101, DecodeStatistics),
                                    quality = Query(handle, id, 19, DecodeQuality),
                                    rssi = Query(handle, id, 0x10000102, b => new { rssiDbm = I32(b, 0) }),
                                    backgroundScanEnabled = Query(handle, id, 2, b => new { enabled = I32(b, 0) != 0 }),
                                    mediaStreamingMode = Query(handle, id, 3, b => new { enabled = I32(b, 0) != 0 }),
                                    radio = Query(handle, id, 4, DecodeRadio)
                                });
                            }
                        }
                    }
                    finally { if (list != IntPtr.Zero) WlanFreeMemory(list); }
                    var powerOk = GetSystemPowerStatus(out var power);
                    snapshots.Add(new { startedAt = started, endedAt = ProbeClock.Now, enumerateCode = enumerate, interfaces,
                        systemPower = new { available = powerOk, acLine = power.AcLine, batteryFlag = power.BatteryFlag,
                            batteryPercent = power.BatteryPercent, energySaverFlag = power.SystemStatusFlag } });
                    await Task.Delay(500);
                } while (watch.Elapsed.TotalSeconds < seconds);
            }
        }
        finally { if (open == 0) closeCode = WlanCloseHandle(handle, IntPtr.Zero); }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            generatedAt = DateTimeOffset.Now, openCode = open, closeCode, negotiatedVersion = version, seconds, snapshots,
            note = "Read-only polling approximately every500ms after query completion. API code0 with unchanged/zero counters is not proof of no radio retries. Counters cover the whole adapter, not a flow. Query duration and native error codes retained. Quality omits SSID/BSSID; no location-gated current_connection or scan. Radio on/off and configured streaming/scan flags do not measure actual NIC sleep/queue residence. System power is not NIC power state."
        }, DiagnosticJson.Readable));
        Console.WriteLine($"WLAN metrics openCode={open} snapshots={snapshots.Count}");
    }

    private static ApiResult Query(IntPtr handle, Guid id, uint opcode, Func<byte[], object> decode)
    {
        var start = ProbeClock.Now;
        var code = WlanQueryInterface(handle, ref id, opcode, IntPtr.Zero, out var size, out var data, out _);
        var end = ProbeClock.Now;
        try
        {
            if (code != 0) return new(code, size, start, end, null, null);
            if (data == IntPtr.Zero || size is < 4 or > 1048576) return new(code, size, start, end, null, "Unexpected native buffer size.");
            var bytes = new byte[size]; Marshal.Copy(data, bytes, 0, bytes.Length);
            try { return new(code, size, start, end, decode(bytes), null); }
            catch (Exception ex) when (ex is ArgumentException or InvalidDataException or OverflowException)
            { return new(code, size, start, end, null, ex.Message); }
        }
        finally { if (data != IntPtr.Zero) WlanFreeMemory(data); }
    }

    private static int I32(byte[] b, int at) => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(at, 4));
    private static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at, 4));
    private static ulong U64(byte[] b, int at) => BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(at, 8));
    private static object DecodeStatistics(byte[] b)
    {
        var count = I32(b, 216);
        if (count is < 0 or > 64 || b.Length < (count == 0 ? 220 : 224 + count * 144)) throw new InvalidDataException("Unexpected WLAN statistics ABI/length.");
        var phys = new List<object>();
        for (var i = 0; i < count; i++)
        {
            var counters = new Dictionary<string, ulong>();
            for (var n = 0; n < PhyNames.Length; n++) counters.Add(PhyNames[n], U64(b, 224 + i * 144 + n * 8));
            phys.Add(new { index = i, counters });
        }
        return new { unicastTx = U64(b, 24), unicastRx = U64(b, 32), multicastTx = U64(b, 120), multicastRx = U64(b, 128), phys };
    }
    private static object DecodeQuality(byte[] b)
    {
        var count = I32(b, 20);
        // SDK26100: WLAN_RATE_SET=256, LINK_INFO=272, links start24.
        if (count is < 0 or > 64 || b.Length < 24 + count * 272 || I32(b, 4) is < 0 or > 100)
            throw new InvalidDataException("Unexpected WLAN quality ABI/length.");
        var links = new List<object>();
        for (var i = 0; i < count; i++)
        {
            var at = 24 + i * 272;
            links.Add(new { linkId = b[at], channelCenterFrequencyRaw = U32(b, at + 4), bandwidthRaw = U32(b, at + 8), rssiDbm = I32(b, at + 12) });
        }
        return new { phyType = I32(b, 0), linkQualityPercent = U32(b, 4), rxRateRaw = U32(b, 8), txRateRaw = U32(b, 12),
            isMlo = I32(b, 16) != 0, links,
            note = "Rates/frequency retain native values. SDK names frequency Mhz while public member text says kHz; no silent unit normalization." };
    }
    private static object DecodeRadio(byte[] b)
    {
        var count = I32(b, 0);
        if (count is < 0 or > 64 || b.Length < 4 + count * 12) throw new InvalidDataException("Unexpected WLAN radio ABI/length.");
        var phys = new List<object>();
        for (var i = 0; i < count; i++) phys.Add(new { index = I32(b, 4 + i * 12), software = I32(b, 8 + i * 12), hardware = I32(b, 12 + i * 12) });
        return new { phys };
    }

    internal static void SelfTest()
    {
        var b = new byte[368]; BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(216), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(224 + 3 * 8), 123);
        var parsed = JsonSerializer.SerializeToElement(DecodeStatistics(b));
        if (parsed.GetProperty("phys")[0].GetProperty("counters").GetProperty("retry").GetUInt64() != 123) throw new InvalidOperationException("Statistics offset regression.");
        var q = new byte[296]; BinaryPrimitives.WriteInt32LittleEndian(q.AsSpan(4), 75); BinaryPrimitives.WriteInt32LittleEndian(q.AsSpan(20), 1); BinaryPrimitives.WriteInt32LittleEndian(q.AsSpan(36), -63);
        if (JsonSerializer.SerializeToElement(DecodeQuality(q)).GetProperty("links")[0].GetProperty("rssiDbm").GetInt32() != -63) throw new InvalidOperationException("Quality offset regression.");
        try { DecodeStatistics(new byte[220]); } catch (Exception) { throw new InvalidOperationException("Zero-PHY header should be accepted."); }
        try { DecodeQuality(new byte[24] { 0,0,0,0, 0,0,0,0, 0,0,0,0, 0,0,0,0, 0,0,0,0, 65,0,0,0 }); throw new InvalidOperationException("Invalid count accepted."); }
        catch (InvalidDataException) { }
        Console.WriteLine("PASS: WLAN statistics/quality offsets, unsigned counters, bounded native lengths");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus { public byte AcLine, BatteryFlag, BatteryPercent, SystemStatusFlag; public uint Life, FullLife; }
    [DllImport("wlanapi.dll")] private static extern uint WlanOpenHandle(uint version, IntPtr reserved, out uint negotiated, out IntPtr handle);
    [DllImport("wlanapi.dll")] private static extern uint WlanCloseHandle(IntPtr handle, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern uint WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] private static extern uint WlanQueryInterface(IntPtr handle, ref Guid id, uint opcode, IntPtr reserved, out uint size, out IntPtr data, out uint valueType);
    [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(IntPtr data);
    [DllImport("kernel32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetSystemPowerStatus(out PowerStatus status);
}
