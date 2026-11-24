// docs\test-vectors の共通ベクタ(Android の単体テストも同じ物を読む)を、Core の実装で確かめる。
// pairing の期待値は、実装とは別に SHA-256 を直接呼んで仕様の式どおりに計算した値とも比べる。
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TabSpan.Core.Tests;

public class VectorTests
{
    private static JsonElement Load(string name) =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "test-vectors", name))).RootElement;

    private static byte[] Hex(JsonElement e, string key) => Convert.FromHexString(e.GetProperty(key).GetString()!);

    [Fact]
    public async Task frames_の枠を符号化と読み取りの両方で確かめる()
    {
        var frames = Load("frames.json").EnumerateArray().Where(e => e.TryGetProperty("payloadHex", out _)).ToList();
        Assert.True(frames.Count >= 8);
        foreach (var e in frames)
        {
            var type = (byte)e.GetProperty("type").GetInt32();
            var payload = Hex(e, "payloadHex");
            var encoded = Hex(e, "encodedHex");

            Assert.Equal(encoded, FrameCodec.Encode(type, payload));
            using var s = new MemoryStream(encoded);
            var f = await FrameCodec.ReadAsync(s, default);
            Assert.Equal(type, f!.Value.Type);
            Assert.Equal(payload, f.Value.Payload.ToArray());
            Assert.Null(await FrameCodec.ReadAsync(s, default));

            AssertPayloadRoundTrips(type, payload);
        }
    }

    // 中身の型で読み、書き直すとバイトまで同じになる(キーの名前と並び、null を書かないこと、エスケープ)
    private static void AssertPayloadRoundTrips(byte type, byte[] payload)
    {
        byte[]? again = (MessageType)type switch
        {
            MessageType.Hello => Messages.ToJson(Messages.FromJson<HelloMsg>(payload)),
            MessageType.HelloAck => Messages.ToJson(Messages.FromJson<HelloAckMsg>(payload)),
            MessageType.PairRequest => Messages.ToJson(Messages.FromJson<PairRequestMsg>(payload)),
            MessageType.PairChallenge => Messages.ToJson(Messages.FromJson<PairChallengeMsg>(payload)),
            MessageType.PairAccept => Messages.ToJson(Messages.FromJson<PairAcceptMsg>(payload)),
            MessageType.PairReject or MessageType.Bye => Messages.ToJson(Messages.FromJson<ReasonMsg>(payload)),
            MessageType.StreamConfig => Messages.ToJson(Messages.FromJson<StreamConfigMsg>(payload)),
            MessageType.Stats => Messages.ToJson(Messages.FromJson<StatsMsg>(payload)),
            MessageType.Notice => Messages.ToJson(Messages.FromJson<NoticeMsg>(payload)),
            MessageType.VideoFrame => VideoAgain(payload),
            MessageType.Ping => Messages.Ping(Messages.ParsePing(payload)),
            MessageType.Pong => PongAgain(payload),
            _ => null, // RequestKeyframe(空)、Touch(Phase 3)、知らない type は枠だけ
        };
        if (again is not null)
        {
            Assert.Equal(Encoding.UTF8.GetString(payload), Encoding.UTF8.GetString(again));
            Assert.Equal(payload, again);
        }
    }

    private static byte[] VideoAgain(byte[] p)
    {
        var v = Messages.ParseVideoFrame(p);
        return Messages.VideoFrame(v.Seq, v.Key, v.CaptureUs, v.SendUs, v.Data.Span);
    }

    private static byte[] PongAgain(byte[] p)
    {
        var (t1, t2) = Messages.ParsePong(p);
        return Messages.Pong(t1, t2);
    }

    [Fact]
    public async Task frames_の長さの上限ちょうどは読めて_超えは中身を読まずに例外()
    {
        var cases = Load("frames.json").EnumerateArray().Where(e => e.TryGetProperty("headerHex", out _)).ToList();
        Assert.Contains(cases, e => !e.GetProperty("tooLarge").GetBoolean());
        Assert.Contains(cases, e => e.GetProperty("tooLarge").GetBoolean());
        foreach (var e in cases)
        {
            var header = Hex(e, "headerHex");
            if (e.GetProperty("tooLarge").GetBoolean())
            {
                using var s = new MemoryStream([.. header, 1, 2, 3]);
                await Assert.ThrowsAsync<FrameTooLargeException>(async () => await FrameCodec.ReadAsync(s, default));
                Assert.Equal(FrameCodec.HeaderSize, s.Position); // ヘッダーの後ろは 1 バイトも読んでいない
            }
            else
            {
                var length = (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(1));
                using var s = new MemoryStream([.. header, .. new byte[length]]);
                var f = await FrameCodec.ReadAsync(s, default);
                Assert.Equal(FrameCodec.MaxPayload, f!.Value.Payload.Length);
            }
        }
    }

    [Fact]
    public void pairing_の_6_桁が実装とも_仕様の式の直接の計算とも合う()
    {
        var cases = Load("pairing.json").EnumerateArray().ToList();
        Assert.True(cases.Count >= 3);
        Assert.Contains(cases, e => e.GetProperty("code").GetString()!.StartsWith('0'));
        foreach (var e in cases)
        {
            var cert = Hex(e, "certDerHex");
            var deviceId = e.GetProperty("deviceId").GetString()!;
            var cn = Hex(e, "clientNonceHex");
            var sn = Hex(e, "serverNonceHex");
            var expected = e.GetProperty("code").GetString()!;

            var hash = SHA256.HashData([.. cert, .. Encoding.UTF8.GetBytes(deviceId), .. cn, .. sn]);
            var direct = (BinaryPrimitives.ReadUInt32BigEndian(hash) % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);

            Assert.Equal(expected, direct);
            Assert.Equal(expected, PairingCode.Compute(cert, deviceId, cn, sn));
        }
    }

    [Fact]
    public void planner_のベクタどおりに計画する()
    {
        var cases = Load("planner.json").EnumerateArray().ToList();
        Assert.True(cases.Count >= 8);
        foreach (var e in cases)
        {
            var src = e.GetProperty("source").EnumerateArray().Select(x => x.GetInt32()).ToArray();
            var supported = e.GetProperty("supported").EnumerateArray()
                .Select(x => x.EnumerateArray().Select(y => y.GetInt32()).ToArray())
                .Select(a => new SizeFps(a[0], a[1], a[2])).ToList();
            var expect = e.GetProperty("expect");
            SizeFps? expected = expect.ValueKind == JsonValueKind.Null
                ? null
                : new SizeFps(expect[0].GetInt32(), expect[1].GetInt32(), expect[2].GetInt32());

            var got = StreamPlanner.Plan(src[0], src[1], supported, e.GetProperty("fpsCap").GetInt32(), e.GetProperty("maxLongEdge").GetInt32());

            Assert.True(expected == got, $"{e}: 期待 {expected}、実際 {got}");
        }
    }

    [Fact]
    public void planner_は負の_maxLongEdge_や_0_の画面で_null()
    {
        SizeFps[] supported = [new(1920, 1080, 60)];
        Assert.Null(StreamPlanner.Plan(2560, 1440, supported, 60, -1));
        Assert.Null(StreamPlanner.Plan(0, 1080, supported, 60, 0));
        Assert.Null(StreamPlanner.Plan(1, 1, supported, 60, 0));
    }

    [Theory]
    [InlineData(2560, 1600, 1920, 1200, 1920, 1200)]
    [InlineData(3840, 2160, 2560, 1600, 2560, 1440)]
    [InlineData(1920, 1080, 2560, 1600, 1920, 1080)]
    public void planner_に仕様の_3_件が入っている(int sw, int sh, int lw, int lh, int ew, int eh)
    {
        Assert.Contains(Load("planner.json").EnumerateArray(), e =>
            e.GetProperty("source")[0].GetInt32() == sw && e.GetProperty("source")[1].GetInt32() == sh
            && e.GetProperty("maxLongEdge").GetInt32() == 0
            && e.GetProperty("expect").ValueKind != JsonValueKind.Null
            && e.GetProperty("supported").GetArrayLength() > 0
            && e.GetProperty("supported").EnumerateArray().Max(x => x[0].GetInt32() * x[1].GetInt32()) == lw * lh
            && e.GetProperty("expect")[0].GetInt32() == ew && e.GetProperty("expect")[1].GetInt32() == eh);
    }
}
