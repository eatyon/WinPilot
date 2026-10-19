using System.Text.Encodings.Web;
using System.Text.Json;

namespace WinPilot.Web;

internal static class ResponseJson
{
    public static readonly JsonSerializerOptions Default = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web) { Encoder = Default.Encoder };
}
