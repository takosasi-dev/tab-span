// 枠の中身(JSON の制御メッセージと、VideoFrame・Ping・Pong のバイナリ)の型と変換。
// JSON はキーが camelCase、null の項目は書かない。null 許容でない項目が無い・null・型違いは FormatException。
using System.Buffers.Binary;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace TabSpan.Core;

public sealed record HelloMsg(int ProtocolVersion, string DeviceId, string? Token, string DeviceName, string AppVersion, ScreenInfo Screen, DecoderInfo Decoder);
public sealed record ScreenInfo(int WidthPx, int HeightPx, int DensityDpi);
public sealed record DecoderInfo(IReadOnlyList<string> Profiles, IReadOnlyList<SizeFps> Supported);
public sealed record SizeFps(int Width, int Height, int Fps);
public sealed record HelloAckMsg(bool Ok, string? Reason, string PcInstanceId, string PcName, string AppVersion, bool TouchEnabled);
public sealed record PairRequestMsg(int ProtocolVersion, string DeviceId, string DeviceName, string ClientNonce); // Base64
public sealed record PairChallengeMsg(string PcInstanceId, string PcName, string ServerNonce);
public sealed record PairAcceptMsg(string Token);
public sealed record ReasonMsg(string Reason); // PairReject と Bye
public sealed record StreamConfigMsg(int Width, int Height, int Fps, string Profile, string Sps, string Pps, string SourceKind); // sps/pps は開始コードなしの NAL を Base64
public sealed record NoticeMsg(string Kind, JsonElement Value);
public sealed record StatsMsg(int Decoded, int Dropped, int DecoderErrors, IReadOnlyList<FrameTiming> Frames);
public sealed record FrameTiming(uint Seq, long ArriveUs, long DecodedUs);

public static class Messages
{
    public const int ProtocolVersion = 1;
    public const int VideoFrameHeaderSize = 21; // seq 4 + flags 1 + captureUs 8 + sendUs 8

    public static readonly string[] Reasons =
        ["version", "auth", "busy", "blocked", "unpaired", "usb_only", "not_accepting", "full", "denied", "no_source", "decoder", "shutdown", "user"];

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        RespectNullableAnnotations = true, // null 許容でない項目の null は例外
        // 日本語の端末名や Base64 の + / を \uXXXX にしない(Android 側とバイトを揃える)
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { RequireNonNullable } },
    };

    // null 許容でない項目は必須にする(無ければ例外)。null 許容の項目(token、reason)は省いてよい。
    private static void RequireNonNullable(JsonTypeInfo info)
    {
        foreach (var p in info.Properties)
        {
            p.IsRequired = !p.IsSetNullable;
        }
    }

    public static byte[] ToJson<T>(T msg) => JsonSerializer.SerializeToUtf8Bytes(msg, Options);

    public static T FromJson<T>(ReadOnlySpan<byte> utf8)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(utf8, Options) ?? throw new FormatException($"{typeof(T).Name} の JSON が null");
        }
        catch (JsonException ex)
        {
            throw new FormatException($"{typeof(T).Name} の JSON が読めない: {ex.Message}", ex);
        }
    }

    public static byte[] VideoFrame(uint seq, bool key, long captureUs, long sendUs, ReadOnlySpan<byte> annexB)
    {
        var p = new byte[VideoFrameHeaderSize + annexB.Length];
        BinaryPrimitives.WriteUInt32BigEndian(p, seq);
        p[4] = key ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt64BigEndian(p.AsSpan(5), captureUs);
        BinaryPrimitives.WriteInt64BigEndian(p.AsSpan(13), sendUs);
        annexB.CopyTo(p.AsSpan(VideoFrameHeaderSize));
        return p;
    }

    public static (uint Seq, bool Key, long CaptureUs, long SendUs, ReadOnlyMemory<byte> Data) ParseVideoFrame(ReadOnlyMemory<byte> p)
    {
        if (p.Length < VideoFrameHeaderSize)
        {
            throw new FormatException($"VideoFrame が {p.Length} バイトしかない");
        }

        var s = p.Span;
        return (BinaryPrimitives.ReadUInt32BigEndian(s), (s[4] & 1) != 0,
            BinaryPrimitives.ReadInt64BigEndian(s[5..]), BinaryPrimitives.ReadInt64BigEndian(s[13..]), p[VideoFrameHeaderSize..]);
    }

    public static byte[] Ping(long t1Us)
    {
        var p = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(p, t1Us);
        return p;
    }

    public static long ParsePing(ReadOnlySpan<byte> p) =>
        p.Length == 8 ? BinaryPrimitives.ReadInt64BigEndian(p) : throw new FormatException($"Ping は 8 バイト(受けたのは {p.Length})");

    public static byte[] Pong(long t1Us, long t2Us)
    {
        var p = new byte[16];
        BinaryPrimitives.WriteInt64BigEndian(p, t1Us);
        BinaryPrimitives.WriteInt64BigEndian(p.AsSpan(8), t2Us);
        return p;
    }

    public static (long T1, long T2) ParsePong(ReadOnlySpan<byte> p) =>
        p.Length == 16
            ? (BinaryPrimitives.ReadInt64BigEndian(p), BinaryPrimitives.ReadInt64BigEndian(p[8..]))
            : throw new FormatException($"Pong は 16 バイト(受けたのは {p.Length})");
}
