// 送る側の流れを、偽の取り込みと偽のエンコーダーで確かめる: StreamConfig → キーフレームの順、止まった画面での RequestKeyframe、
// 詰まり(FR-17)で捨てた後はキーフレーム、設定の変更(FR-15)で StreamConfig から、どの大きさも復号できなければ decoder。
using System.Threading.Channels;
using TabSpan.Core;

namespace TabSpan.Platform.Tests;

public sealed class MirrorStreamerTests
{
    private static readonly byte[] MainSps = Convert.FromHexString("674d401fd9005005bb0110000003001000000303c0f1832480");
    private static readonly DecoderInfo Decoder = new(["baseline", "main"], [new SizeFps(1280, 720, 60), new SizeFps(1920, 1080, 30)]);
    private static readonly StreamOptions Options = new(60, 10, 0, 3);

    private sealed class FakeCapture(Func<bool> next) : ICaptureSource
    {
        public int Width => 1920;
        public int Height => 1080;
        public bool Lost => false;

        public bool TryTake(byte[] dest, int width, int height, int waitMs, out long captureUs)
        {
            captureUs = SystemClock.Instance.NowUs;
            if (next())
            {
                return true;
            }

            Thread.Sleep(waitMs);
            return false;
        }

        public void Dispose()
        {
        }
    }

    /// <summary>入れた順の番号を中身の最後から 2 番目に入れる。キーフレームには SPS と PPS を付ける。</summary>
    private sealed class FakeEncoder(EncoderSettings settings, List<EncoderSettings> opened) : IVideoEncoder
    {
        private byte _index;

        public string Name => "偽物";
        public bool IsHardware => false;
        public EncoderSettings Settings { get; } = Record(settings, opened);

        public EncodedFrame? Encode(byte[] nv12, long timeUs, bool forceKey)
        {
            var index = _index++;
            byte[] data = forceKey
                ? [0, 0, 0, 1, .. MainSps, 0, 0, 0, 1, 0x68, 0xCE, 0x3C, 0x80, 0, 0, 0, 1, 0x65, 0x88, index, 0x80]
                : [0, 0, 0, 1, 0x41, 0x9A, index, 0x80];
            return new EncodedFrame(data, forceKey);
        }

        public void Dispose()
        {
        }

        private static EncoderSettings Record(EncoderSettings s, List<EncoderSettings> opened)
        {
            lock (opened)
            {
                opened.Add(s);
            }

            return s;
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();

        public Rig(Func<bool> next, DecoderInfo? decoder = null, Func<Task>? gate = null)
        {
            Streamer = new MirrorStreamer(async (type, payload, ct) =>
                {
                    if (gate is not null)
                    {
                        await gate();
                    }

                    await Sent.Writer.WriteAsync(new Frame((byte)type, payload.ToArray()), ct);
                },
                decoder ?? Decoder, () => new FakeCapture(next), s => new FakeEncoder(s, Opened), false, Options, SystemClock.Instance, _ => { }, () => 0);
            Run = Streamer.RunAsync(_cts.Token);
        }

        public MirrorStreamer Streamer { get; }
        public Task Run { get; }
        public List<EncoderSettings> Opened { get; } = [];
        public Channel<Frame> Sent { get; } = Channel.CreateUnbounded<Frame>();

        public async Task<Frame> NextAsync(MessageType type)
        {
            using var timeout = new CancellationTokenSource(5000);
            while (true)
            {
                var f = await Sent.Reader.ReadAsync(timeout.Token);
                if (f.Type == (byte)type)
                {
                    return f;
                }
            }
        }

        public async Task<(uint Seq, bool Key, byte Index)> NextVideoAsync()
        {
            var v = Messages.ParseVideoFrame((await NextAsync(MessageType.VideoFrame)).Payload);
            return (v.Seq, v.Key, v.Data.Span[^2]); // 最後の 0 は NAL の後ろの 0 として削られるので、番号の後ろに 0x80 を置いた
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            await Run;
        }
    }

    [Fact]
    public async Task Stream_config_comes_first_with_a_fixed_sps_then_a_keyframe()
    {
        await using var rig = new Rig(() => true);
        var first = await rig.Sent.Reader.ReadAsync();
        Assert.Equal((byte)MessageType.Notice, first.Type);
        Assert.Equal("softwareEncoder", Messages.FromJson<NoticeMsg>(first.Payload.Span).Kind);

        var config = Messages.FromJson<StreamConfigMsg>((await rig.Sent.Reader.ReadAsync()).Payload.Span);
        Assert.Equal((1280, 720, 60, "main", "physical"), (config.Width, config.Height, config.Fps, config.Profile, config.SourceKind));
        Assert.Equal(SpsVui.AddNoReorder(MainSps), Convert.FromBase64String(config.Sps));
        Assert.Equal([0x68, 0xCE, 0x3C, 0x80], Convert.FromBase64String(config.Pps));

        var key = Messages.ParseVideoFrame((await rig.Sent.Reader.ReadAsync()).Payload);
        Assert.Equal((0u, true), (key.Seq, key.Key));
        Assert.Equal(AnnexB.SplitNals(key.Data)[0].ToArray(), SpsVui.AddNoReorder(MainSps)); // キーフレームの前の SPS も直っている
        var next = await rig.NextVideoAsync();
        Assert.Equal((1u, false), (next.Seq, next.Key));
        Assert.Equal(new EncoderSettings(1280, 720, 60, 10_000_000, "main"), Assert.Single(rig.Opened));
    }

    [Fact]
    public async Task Request_keyframe_on_a_still_screen_reencodes_the_last_picture()
    {
        var frames = 0;
        await using var rig = new Rig(() => Interlocked.Increment(ref frames) <= 2);
        Assert.True((await rig.NextVideoAsync()).Key);
        Assert.False((await rig.NextVideoAsync()).Key);
        rig.Streamer.OnFrame(new Frame((byte)MessageType.RequestKeyframe, Array.Empty<byte>()));
        rig.Streamer.OnFrame(new Frame((byte)MessageType.RequestKeyframe, Array.Empty<byte>())); // 1 秒の間の 2 回目は捨てる
        Assert.True((await rig.NextVideoAsync()).Key);
        await Task.Delay(300);
        Assert.False(rig.Sent.Reader.TryRead(out _)); // 2 回目の分は出ない
    }

    [Fact]
    public async Task Backlog_drops_everything_and_restarts_from_a_keyframe()
    {
        var open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var rig = new Rig(() => true, gate: () => Interlocked.Increment(ref calls) <= 3 ? Task.CompletedTask : open.Task);
        await Task.Delay(400); // 送れない間に 60 fps で溜まる
        open.SetResult();

        var videos = new List<(uint Seq, bool Key, byte Index)>();
        for (var i = 0; i < 12; i++)
        {
            videos.Add(await rig.NextVideoAsync());
        }

        Assert.True(rig.Streamer.TakeSecond().BacklogDropped > 0);
        for (var i = 1; i < videos.Count; i++)
        {
            if (videos[i].Index != (byte)(videos[i - 1].Index + 1))
            {
                Assert.True(videos[i].Key, $"捨てた後の {videos[i].Index} がキーフレームでない");
            }
        }
    }

    [Fact]
    public async Task Changing_options_resends_stream_config_and_a_keyframe()
    {
        await using var rig = new Rig(() => true);
        await rig.NextAsync(MessageType.StreamConfig);
        await rig.NextVideoAsync();
        rig.Streamer.Update(Options with { FpsCap = 30 });
        var config = Messages.FromJson<StreamConfigMsg>((await rig.NextAsync(MessageType.StreamConfig)).Payload.Span);
        Assert.Equal((1920, 1080, 30), (config.Width, config.Height, config.Fps));
        var first = await rig.NextVideoAsync();
        Assert.Equal((0u, true), (first.Seq, first.Key));
    }

    [Fact]
    public async Task Nothing_decodable_stops_with_decoder()
    {
        var rig = new Rig(() => true, decoder: new DecoderInfo(["main"], []));
        var ex = await Assert.ThrowsAsync<StreamStoppedException>(() => rig.Run);
        Assert.Equal("decoder", ex.Reason);
    }

    [Theory]
    [InlineData(new[] { "baseline", "main", "high" }, "high")]
    [InlineData(new[] { "baseline", "main" }, "main")]
    [InlineData(new string[0], "baseline")]
    public void Profile_is_high_then_main_then_baseline(string[] profiles, string expected) =>
        Assert.Equal(expected, MirrorStreamer.ChooseProfile(new DecoderInfo(profiles, [])));
}
