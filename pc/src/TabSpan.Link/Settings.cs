// PC 側の設定(仕様 §9.2)と、その読み書き(FR-26)。範囲の外の値は表の値に直す。
// 読めない(壊れた JSON、知らない schemaVersion)ときは既定値にし、元のファイルを settings.broken-<時刻>.json に残す。
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using TabSpan.Core;

namespace TabSpan.Link;

public sealed record Settings
{
    public const int CurrentSchema = 1;

    public int SchemaVersion { get; init; } = CurrentSchema;
    public int Port { get; init; } = 47815;
    public bool UsbEnabled { get; init; } = true;
    public bool LanEnabled { get; init; }
    public bool AutoLaunchAndroidApp { get; init; } = true;
    public string AdbPath { get; init; } = "";
    public int AdbPollSec { get; init; } = 3;
    public string SourceMonitor { get; init; } = "";
    public int FpsCap { get; init; } = 60;
    public int BitrateMbps { get; init; } = 10;
    public int MaxLongEdge { get; init; }
    public bool TouchEnabled { get; init; } = true;
    public string TouchHotkey { get; init; } = "Ctrl+Alt+T";
    public int TouchRefreshMs { get; init; } = 100;
    public bool DetachOnDisconnect { get; init; } = true;
    public int DetachDelaySec { get; init; } = 30;
    public int BacklogLimitFrames { get; init; } = 3;
    public bool StartWithWindows { get; init; }
    public string Language { get; init; } = "auto";
    public IReadOnlyList<string> VirtualMonitorMatch { get; init; } = [];

    /// <summary>§9.2 の表のとおり、範囲の外の値を既定に直す。JSON の null も既定に直す。</summary>
    public Settings Normalize() => this with
    {
        Port = Port is >= 1024 and <= 65535 ? Port : 47815,
        AdbPollSec = AdbPollSec is >= 1 and <= 30 ? AdbPollSec : 3,
        FpsCap = FpsCap is 30 or 60 ? FpsCap : 60,
        BitrateMbps = BitrateMbps is >= 2 and <= 40 ? BitrateMbps : 10,
        MaxLongEdge = MaxLongEdge is 0 or 1280 or 1920 ? MaxLongEdge : 0,
        TouchRefreshMs = TouchRefreshMs is >= 20 and <= 500 ? TouchRefreshMs : 100,
        DetachDelaySec = DetachDelaySec is >= 5 and <= 600 ? DetachDelaySec : 30,
        BacklogLimitFrames = BacklogLimitFrames is >= 1 and <= 30 ? BacklogLimitFrames : 3,
        Language = Language is "auto" or "ja" or "en" ? Language : "auto",
        AdbPath = AdbPath ?? "",
        SourceMonitor = SourceMonitor ?? "",
        TouchHotkey = TouchHotkey ?? "Ctrl+Alt+T",
        VirtualMonitorMatch = VirtualMonitorMatch ?? [],
    };
}

public sealed class SettingsStore(string path, IClock clock)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    /// <summary>設定を読む。壊れていたら既定値と、残した元のファイルの場所(呼ぶ側がログに書く)。</summary>
    public (Settings Settings, string? BrokenCopy) Load()
    {
        if (!File.Exists(path))
        {
            return (new Settings(), null);
        }

        Settings? loaded;
        try
        {
            loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllBytes(path), Options);
        }
        catch (JsonException)
        {
            loaded = null;
        }

        if (loaded is null || loaded.SchemaVersion != Settings.CurrentSchema)
        {
            return (new Settings(), AtomicFile.SetAside(path, clock.UtcNow));
        }

        return (loaded.Normalize(), null);
    }

    public void Save(Settings settings) => AtomicFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(settings, Options));
}
