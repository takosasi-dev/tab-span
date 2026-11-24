// 端末の台帳 devices.json の読み書き(仕様 §9.2)。表そのものと照合は Core の DeviceRegistry。
// 読めないときは *.broken-<時刻> に残して空から始める(§10)。トークンはハッシュだけを置く(INV-11)。
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using TabSpan.Core;

namespace TabSpan.Link;

public static class DeviceStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All), // 端末の名前の日本語を \u にしない
    };

    public static (List<DeviceEntry> Entries, string? BrokenCopy) Load(string path, IClock clock)
    {
        if (!File.Exists(path))
        {
            return ([], null);
        }

        try
        {
            var entries = JsonSerializer.Deserialize<List<DeviceEntry>>(File.ReadAllBytes(path), Options);
            if (entries is not null && entries.All(e => e is { DeviceId: not null, Name: not null, TokenSha256.Length: 64 }))
            {
                return (entries, null);
            }
        }
        catch (JsonException)
        {
        }

        return ([], AtomicFile.SetAside(path, clock.UtcNow));
    }

    public static void Save(string path, IEnumerable<DeviceEntry> entries) =>
        AtomicFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(entries.ToList(), Options));
}
