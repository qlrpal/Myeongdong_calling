using System.Text.Encodings.Web;
using System.Text.Json;

namespace VoiceNative;

internal static class DiagnosticJson
{
    // Human-readable JSON reports only. Never embed this output in HTML.
    public static readonly JsonSerializerOptions Readable = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
