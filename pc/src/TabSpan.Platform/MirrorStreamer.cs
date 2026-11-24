// 送る側の流れ: 取り込み → NV12(縮小) → H.264 → 枠。送り先は delegate で受ける(Link を参照しない)。
// 大きさは StreamPlanner(§9.7)、キーフレームは KeyframePolicy(FR-16)、詰まりは BacklogPolicy(FR-17)、設定の変更で作り直し(FR-15)。
// 取り込みと圧縮は専用のスレッド 1 本で作り・使い・捨てる。送るのは別の非同期のループで、待ちの列にはいちばん新しい物だけが溜まる(D-13)。
using System.Text.Json;
using TabSpan.Core;

namespace TabSpan.Platform;

public delegate ValueTask SendFrame(MessageType type, ReadOnlyMemory<byte> payload, CancellationToken ct);

/// <summary>設定のうち映像に効く物(FR-15)。</summary>
public sealed record StreamOptions(int FpsCap, int BitrateMbps, int MaxLongEdge, int BacklogLimitFrames);

/// <summary>配信を止めた理由。Reason は Bye に入れる値(decoder)か、no_source(Notice(paused) を送って待つ)。</summary>
public sealed class StreamStoppedException : Exception
{
    public StreamStoppedException()
    {
        Reason = "no_source";
    }

    public StreamStoppedException(string reason) : base(reason)
    {
        Reason = reason;
    }

    public StreamStoppedException(string reason, Exception inner) : base(reason, inner)
    {
        Reason = reason;
    }

    public string Reason { get; }
}

/// <summary>1 秒ぶんの送る側の数字(serve の 1 行、画面)。</summary>
public sealed record StreamSecond(int Frames, int KeyFrames, long Bytes, long BacklogDropped, LatencySummary CaptureToSend, LatencySummary Total,
    int DeviceDecoded, int DeviceDropped, int Width, int Height, int Fps, string Encoder, bool HardwareEncoder)
{
    /// <summary>§9.9 の送信→到着(時計のずれで PC の時計に直す)。</summary>
    public LatencySummary SendToArrive { get; init; } = new(0, 0, 0);

    /// <summary>§9.9 の到着→復号(端末の時計だけで出る)。</summary>
    public LatencySummary ArriveToDecode { get; init; } = new(0, 0, 0);
}

public sealed class MirrorStreamer
{
    private static readonly int[] LongEdgeSteps = [0, 1920, 1280];

    private readonly SendFrame _send;
    private readonly DecoderInfo _decoder;
    private readonly Func<ICaptureSource> _openCapture;
    private readonly Func<EncoderSettings, IVideoEncoder> _openEncoder;
    private readonly string _sourceKind;
    private readonly IClock _clock;
    private readonly Action<string> _log;
    private readonly Func<long?> _offsetUs;
    private readonly KeyframePolicy _keyframes;
    private readonly SendQueue _queue = new();
    private readonly Lock _statsLock = new();
    private readonly Dictionary<uint, (long Capture, long Send)> _sent = [];
    private readonly LatencyStats _captureToSend = new();
    private readonly LatencyStats _total = new();
    private readonly LatencyStats _sendToArrive = new();
    private readonly LatencyStats _arriveToDecode = new();
    private readonly Queue<(long At, int Errors)> _decoderErrors = new();
    private StreamOptions _options;
    private StreamOptions? _pendingOptions;
    private BacklogPolicy _backlog;
    private int _maxLongEdgeOverride = -1; // §9.7 の末尾の一時的な段下げ。-1 = 無し
    private volatile bool _stepDown;
    private SizeFps _plan = new(0, 0, 0);
    private string _encoderName = "";
    private bool _hardware;
    private int _frames;
    private int _keyFrames;
    private long _bytes;
    private int _deviceDecoded;
    private int _deviceDropped;

    public MirrorStreamer(SendFrame send, DecoderInfo decoder, Func<ICaptureSource> openCapture, Func<EncoderSettings, IVideoEncoder> openEncoder,
        bool sourceIsVirtual, StreamOptions options, IClock clock, Action<string> log, Func<long?> clockOffsetUs)
    {
        _send = send;
        _decoder = decoder;
        _openCapture = openCapture;
        _openEncoder = openEncoder;
        _sourceKind = sourceIsVirtual ? "virtual" : "physical";
        _options = options;
        _backlog = new BacklogPolicy(options.BacklogLimitFrames);
        _clock = clock;
        _log = log;
        _offsetUs = clockOffsetUs;
        _keyframes = new KeyframePolicy(clock);
    }

    /// <summary>D-6: 端末が名乗った中から high → main → baseline。</summary>
    public static string ChooseProfile(DecoderInfo decoder) =>
        new[] { "high", "main", "baseline" }.FirstOrDefault(p => decoder.Profiles.Contains(p, StringComparer.OrdinalIgnoreCase)) ?? "baseline";

    /// <summary>FR-15: 映している最中の設定の変更。エンコーダーを作り直し、StreamConfig からやり直す。</summary>
    public void Update(StreamOptions options) => Volatile.Write(ref _pendingOptions, options);

    /// <summary>端末からの枠(RequestKeyframe・Stats)。Touch は Phase 3 なので受け流す。</summary>
    public void OnFrame(Frame frame)
    {
        switch ((MessageType)frame.Type)
        {
            case MessageType.RequestKeyframe:
                _keyframes.OnRequest(); // 1 秒に 1 回まで
                break;
            case MessageType.Stats:
                try
                {
                    OnStats(Messages.FromJson<StatsMsg>(frame.Payload.Span));
                }
                catch (FormatException ex)
                {
                    _log($"Stats を読めない: {ex.Message}");
                }

                break;
        }
    }

    public StreamSecond TakeSecond()
    {
        lock (_statsLock)
        {
            var s = new StreamSecond(_frames, _keyFrames, _bytes, _backlog.Dropped, _captureToSend.TakeSecond(), _total.TakeSecond(),
                _deviceDecoded, _deviceDropped, _plan.Width, _plan.Height, _plan.Fps, _encoderName, _hardware)
            {
                SendToArrive = _sendToArrive.TakeSecond(),
                ArriveToDecode = _arriveToDecode.TakeSecond(),
            };
            (_frames, _keyFrames, _bytes, _deviceDecoded, _deviceDropped) = (0, 0, 0, 0, 0);
            return s;
        }
    }

    /// <summary>ct で止めるまで映す。止める理由があれば StreamStoppedException。</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                CaptureLoop(stop.Token);
                done.TrySetResult();
            }
#pragma warning disable CA1031 // 取り込みのスレッドの例外は、握りつぶさずに RunAsync の呼び出し元へ渡す
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
#pragma warning restore CA1031
            finally
            {
                stop.Cancel();
                _queue.Wake();
            }
        })
        { IsBackground = true, Name = "TabSpan capture" };
        thread.Start();
        try
        {
            await SendLoopAsync(stop.Token).ConfigureAwait(false);
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
        }

        try
        {
            await done.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private void CaptureLoop(CancellationToken ct)
    {
        ICaptureSource? capture = null;
        IVideoEncoder? encoder = null;
        Windows.Win32.PInvoke.timeBeginPeriod(1); // Sleep(1) を 1 ms 前後にする
        try
        {
            try
            {
                capture = _openCapture();
            }
            catch (CaptureUnavailableException ex)
            {
                throw new StreamStoppedException("no_source", ex);
            }

            var nv12 = Array.Empty<byte>();
            var haveImage = false;
            var needConfig = true;
            var nextDueUs = 0L;
            var periodUs = 0L;
            var lastEncoder = "";
            var profile = ChooseProfile(_decoder);

            void Restart()
            {
                if (Interlocked.Exchange(ref _pendingOptions, null) is { } o)
                {
                    _options = o;
                    _backlog = new BacklogPolicy(o.BacklogLimitFrames);
                }

                if (_stepDown)
                {
                    _stepDown = false;
                    var current = EffectiveMaxLongEdge();
                    var i = Array.IndexOf(LongEdgeSteps, current);
                    if (i < 0 || i == LongEdgeSteps.Length - 1)
                    {
                        throw new StreamStoppedException("decoder");
                    }

                    _maxLongEdgeOverride = LongEdgeSteps[i + 1];
                    _log($"端末のデコーダーのエラーが続いたので、長い辺の上限を {_maxLongEdgeOverride} に下げる");
                }

                var plan = StreamPlanner.Plan(capture!.Width, capture.Height, _decoder.Supported, _options.FpsCap, EffectiveMaxLongEdge())
                    ?? throw new StreamStoppedException("decoder");
                encoder?.Dispose();
                encoder = null;
                encoder = _openEncoder(new EncoderSettings(plan.Width, plan.Height, plan.Fps, _options.BitrateMbps * 1_000_000, profile));
                lock (_statsLock)
                {
                    _plan = plan;
                }

                _log($"送る大きさ: {plan.Width}x{plan.Height} {plan.Fps} fps({profile}、{_options.BitrateMbps} Mbps)。画面は {capture.Width}x{capture.Height}");
                if (nv12.Length != Nv12.Size(plan.Width, plan.Height))
                {
                    nv12 = new byte[Nv12.Size(plan.Width, plan.Height)];
                    haveImage = false;
                }

                periodUs = 1_000_000 / plan.Fps;
                needConfig = true;
                lastEncoder = "";
                _queue.DropVideo();
                _keyframes.MarkStreamStart();
            }

            Restart();
            while (!ct.IsCancellationRequested)
            {
                if (capture!.Lost)
                {
                    capture.Dispose();
                    capture = null;
                    try
                    {
                        capture = _openCapture(); // 解像度が変わっただけなら開き直せる
                    }
                    catch (CaptureUnavailableException ex)
                    {
                        throw new StreamStoppedException("no_source", ex);
                    }

                    Restart();
                }

                if (Volatile.Read(ref _pendingOptions) is not null || _stepDown)
                {
                    Restart();
                }

                // fps の刻み(165 Hz の画面でも 30/60 に抑える)
                var now = _clock.NowUs;
                if (now < nextDueUs)
                {
                    Thread.Sleep((int)Math.Max(1, (nextDueUs - now) / 1000));
                    continue;
                }

                var got = capture.TryTake(nv12, _plan.Width, _plan.Height, 20, out var captureUs);
                bool force;
                if (got)
                {
                    haveImage = true;
                    force = _keyframes.TakeForceKey();
                }
                else if (haveImage && _keyframes.TakeForceKey())
                {
                    // 画面が止まっていると新しい絵が来ない。持っている 1 枚をキーフレームとして作り直す
                    force = true;
                    captureUs = _clock.NowUs;
                }
                else
                {
                    continue;
                }

                nextDueUs = Math.Max(nextDueUs + periodUs, _clock.NowUs - periodUs);
                var frame = encoder!.Encode(nv12, captureUs, force);
                if (encoder.Name != lastEncoder)
                {
                    // エンコーダーが決まった・替わった。SPS が変わるので StreamConfig からやり直す
                    lastEncoder = encoder.Name;
                    needConfig = true;
                    lock (_statsLock)
                    {
                        (_encoderName, _hardware) = (encoder.Name, encoder.IsHardware);
                    }

                    _queue.Enqueue(new Outgoing(MessageType.Notice, Messages.ToJson(new NoticeMsg("softwareEncoder", JsonSerializer.SerializeToElement(!encoder.IsHardware)))));
                }

                if (frame is null)
                {
                    if (force)
                    {
                        _keyframes.ForceNext();
                    }

                    continue;
                }

                if (needConfig)
                {
                    if (!frame.Key || ExtractParameterSets(frame.AnnexB) is not { } sets)
                    {
                        _keyframes.ForceNext(); // StreamConfig の直後はキーフレームから
                        continue;
                    }

                    _queue.Enqueue(new Outgoing(MessageType.StreamConfig, Messages.ToJson(new StreamConfigMsg(_plan.Width, _plan.Height, _plan.Fps, profile,
                        Convert.ToBase64String(sets.Sps), Convert.ToBase64String(sets.Pps), _sourceKind))));
                    needConfig = false;
                }

                var data = frame.Key ? RewriteKeyframe(frame.AnnexB, _log) : frame.AnnexB;
                if (_queue.Enqueue(new Outgoing(MessageType.VideoFrame, data, captureUs, frame.Key), _backlog))
                {
                    _keyframes.ForceNext(); // FR-17: 待ちを捨てたので次はキーフレーム
                }
            }
        }
        finally
        {
            Windows.Win32.PInvoke.timeEndPeriod(1);
            encoder?.Dispose();
            capture?.Dispose();
        }
    }

    private int EffectiveMaxLongEdge() => _maxLongEdgeOverride < 0 ? _options.MaxLongEdge
        : _options.MaxLongEdge == 0 ? _maxLongEdgeOverride : Math.Min(_options.MaxLongEdge, _maxLongEdgeOverride);

    private async Task SendLoopAsync(CancellationToken ct)
    {
        uint seq = 0;
        while (await _queue.TakeAsync(ct).ConfigureAwait(false) is { } o)
        {
            if (o.Type == MessageType.VideoFrame)
            {
                var sendUs = _clock.NowUs;
                var payload = Messages.VideoFrame(seq, o.Key, o.CaptureUs, sendUs, o.Payload);
                lock (_statsLock)
                {
                    _sent[seq] = (o.CaptureUs, sendUs);
                    _sent.Remove(seq - 600);
                    _captureToSend.Add(sendUs - o.CaptureUs);
                    _frames++;
                    _keyFrames += o.Key ? 1 : 0;
                    _bytes += payload.Length;
                }

                seq++;
                await _send(MessageType.VideoFrame, payload, ct).ConfigureAwait(false);
            }
            else
            {
                if (o.Type == MessageType.StreamConfig)
                {
                    seq = 0; // §9.3: StreamConfig を送るたびに 0 から
                    lock (_statsLock)
                    {
                        _sent.Clear();
                    }
                }

                await _send(o.Type, o.Payload, ct).ConfigureAwait(false);
            }
        }
    }

    private void OnStats(StatsMsg stats)
    {
        var offset = _offsetUs();
        var now = _clock.NowUs;
        lock (_statsLock)
        {
            _deviceDecoded += stats.Decoded;
            _deviceDropped += stats.Dropped;
            foreach (var f in stats.Frames)
            {
                _arriveToDecode.Add(f.DecodedUs - f.ArriveUs);
                if (offset is { } off && _sent.TryGetValue(f.Seq, out var t))
                {
                    // §9.9: 取り込み→送信 + 送信→到着 + 到着→復号
                    _sendToArrive.Add(f.ArriveUs - off - t.Send);
                    _total.Add(t.Send - t.Capture + (f.ArriveUs - off - t.Send) + (f.DecodedUs - f.ArriveUs));
                }
            }

            // §9.7 の末尾: 5 秒の間にデコーダーのエラーが 3 回で、長い辺の上限を 1 段下げる(decoderErrors は 1 秒ぶんの数と読む)
            if (stats.DecoderErrors > 0)
            {
                _decoderErrors.Enqueue((now, stats.DecoderErrors));
            }

            while (_decoderErrors.Count > 0 && _decoderErrors.Peek().At < now - 5_000_000)
            {
                _decoderErrors.Dequeue();
            }

            if (_decoderErrors.Sum(e => e.Errors) >= 3)
            {
                _decoderErrors.Clear();
                _stepDown = true;
            }
        }
    }

    /// <summary>キーフレームの Annex B から SPS と PPS(開始コードなし)を取る。SPS は並べ替えなしの VUI に直す。</summary>
    internal static (byte[] Sps, byte[] Pps)? ExtractParameterSets(byte[] annexB)
    {
        byte[]? sps = null, pps = null;
        foreach (var nal in AnnexB.SplitNals(annexB))
        {
            switch (nal.Span[0] & 0x1F)
            {
                case 7:
                    sps ??= nal.ToArray();
                    break;
                case 8:
                    pps ??= nal.ToArray();
                    break;
            }
        }

        return sps is null || pps is null ? null : (FixSps(sps, null), pps);
    }

    /// <summary>キーフレームの SPS を並べ替えなしの物に差し替えて組み直す(端末のデコーダーが絵を溜めないように)。</summary>
    internal static byte[] RewriteKeyframe(byte[] annexB, Action<string>? log)
    {
        var output = new List<byte>(annexB.Length + 16);
        foreach (var nal in AnnexB.SplitNals(annexB))
        {
            output.AddRange((ReadOnlySpan<byte>)[0, 0, 0, 1]);
            output.AddRange((nal.Span[0] & 0x1F) == 7 ? FixSps(nal.ToArray(), log) : nal.Span);
        }

        return [.. output];
    }

    private static byte[] FixSps(byte[] sps, Action<string>? log)
    {
        try
        {
            return SpsVui.AddNoReorder(sps);
        }
        catch (InvalidDataException ex)
        {
            log?.Invoke($"SPS を書き換えられないので元のまま送る: {ex.Message}");
            return sps;
        }
    }

    private sealed record Outgoing(MessageType Type, byte[] Payload, long CaptureUs = 0, bool Key = false);

    /// <summary>送る待ちの列。映像は詰まったら全部捨てる(FR-17)。制御の枠(StreamConfig・Notice)は捨てない。</summary>
    private sealed class SendQueue
    {
        private readonly Lock _lock = new();
        private readonly List<Outgoing> _items = [];
        private readonly SemaphoreSlim _signal = new(0);

        public void Enqueue(Outgoing o)
        {
            lock (_lock)
            {
                _items.Add(o);
            }

            _signal.Release();
        }

        /// <summary>映像を足す。待ちの映像が上限を超えたら全部捨てて true。</summary>
        public bool Enqueue(Outgoing o, BacklogPolicy backlog)
        {
            lock (_lock)
            {
                _items.Add(o);
                if (backlog.ShouldFlush(_items.Count(i => i.Type == MessageType.VideoFrame)))
                {
                    _items.RemoveAll(i => i.Type == MessageType.VideoFrame);
                    return true;
                }
            }

            _signal.Release();
            return false;
        }

        public void DropVideo()
        {
            lock (_lock)
            {
                _items.RemoveAll(i => i.Type == MessageType.VideoFrame);
            }
        }

        public void Wake() => _signal.Release();

        /// <summary>次の枠。止めたら null。</summary>
        public async Task<Outgoing?> TakeAsync(CancellationToken ct)
        {
            while (true)
            {
                try
                {
                    await _signal.WaitAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }

                lock (_lock)
                {
                    if (_items.Count > 0)
                    {
                        var o = _items[0];
                        _items.RemoveAt(0);
                        return o;
                    }
                }
            }
        }
    }
}
