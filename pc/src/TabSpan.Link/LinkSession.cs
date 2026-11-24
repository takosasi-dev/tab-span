// 認証の済んだ 1 本のセッション: 1 秒ごとの Ping と 5 秒の Pong の時間切れ、受けた枠の振り分け(知らない type は読み飛ばす)、Bye。
// 枠の書き込みは 1 本にまとめる(SslStream は同時に書けない。ヘッダーと中身を 1 回で書く)。
// Link が扱わない枠(RequestKeyframe・Stats・Touch)は Incoming で外へ回す。外の処理は SendAsync で枠を差し込む。
using System.Net;
using System.Threading.Channels;
using TabSpan.Core;

namespace TabSpan.Link;

/// <summary>1 本の流れへの書き込みを 1 つずつにする。</summary>
internal sealed class FrameWriter(Stream stream)
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async ValueTask SendAsync(MessageType type, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var bytes = FrameCodec.Encode((byte)type, payload.Span);
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>断りや Bye のように、届かなくてもよい枠を 1 秒だけ試す。</summary>
    public async ValueTask TrySendAsync(MessageType type, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            await SendAsync(type, payload, limit.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // 相手が先に閉じた。閉じる処理はこのまま続ける
        }
    }
}

public sealed class LinkSession
{
    private readonly Stream _stream;
    private readonly FrameWriter _writer;
    private readonly SessionMachine _machine;
    private readonly LinkOptions _options;
    private readonly IClock _clock;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _closing = new();
    private readonly Channel<Frame> _incoming = Channel.CreateBounded<Frame>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });
    private long _lastPongUs;
    private int _closed;

    internal LinkSession(Stream stream, FrameWriter writer, IPAddress remote, HelloMsg hello, SessionMachine machine, LinkOptions options, IClock clock, Action<string> log)
    {
        _stream = stream;
        _writer = writer;
        _machine = machine;
        _options = options;
        _clock = clock;
        _log = log;
        Hello = hello;
        IsUsb = IPAddress.IsLoopback(remote);
    }

    public HelloMsg Hello { get; }

    public string DeviceName => Hello.DeviceName;

    /// <summary>リモートがループバックなら USB(adb reverse)、それ以外は LAN。</summary>
    public bool IsUsb { get; }

    /// <summary>Ping/Pong から出した往復の時間と時計のずれ(RttUs・OffsetUs)。</summary>
    public LatencyStats Latency { get; } = new();

    /// <summary>Link が扱わない端末からの枠(RequestKeyframe・Stats・Touch)。セッションが終わると閉じる。</summary>
    public ChannelReader<Frame> Incoming => _incoming.Reader;

    /// <summary>セッションが終わるときに取り消される。</summary>
    public CancellationToken Closing => _closing.Token;

    /// <summary>枠を 1 つ送る。認証の後にしか、この口は無い(INV-4)。</summary>
    public ValueTask SendAsync(MessageType type, ReadOnlyMemory<byte> payload, CancellationToken ct) => _writer.SendAsync(type, payload, ct);

    /// <summary>Bye(reason) を送って閉じる。reason が null なら送らずに閉じる。何度呼んでもよい。</summary>
    public async Task CloseAsync(string? reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1)
        {
            return;
        }

        if (reason is not null)
        {
            _log($"Bye({reason}) を送って閉じる");
            await _writer.TrySendAsync(MessageType.Bye, Messages.ToJson(new ReasonMsg(reason)), CancellationToken.None).ConfigureAwait(false);
        }

        _machine.Close();
        await _closing.CancelAsync().ConfigureAwait(false);
        _incoming.Writer.TryComplete();
        await _stream.DisposeAsync().ConfigureAwait(false); // 読みと書きの待ちを終わらせる
    }

    internal async Task RunAsync(Func<LinkSession, CancellationToken, Task> handler, CancellationToken serverCt)
    {
        Volatile.Write(ref _lastPongUs, _clock.NowUs);
        using var link = CancellationTokenSource.CreateLinkedTokenSource(serverCt, _closing.Token);
        var tasks = new[] { ReadLoopAsync(link.Token), PingLoopAsync(link.Token), RunHandlerAsync(handler, link.Token) };
        await Task.WhenAny(tasks).ConfigureAwait(false);
        await CloseAsync(serverCt.IsCancellationRequested ? "shutdown" : null).ConfigureAwait(false);
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task RunHandlerAsync(Func<LinkSession, CancellationToken, Task> handler, CancellationToken ct)
    {
        try
        {
            await handler(this, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
#pragma warning disable CA1031 // 外の処理(映像)の失敗でアプリを落とさない。ログに残してセッションを閉じる
        catch (Exception ex)
        {
            _log($"セッションの処理が止まった: {ex.GetType().Name}: {ex.Message}");
        }
#pragma warning restore CA1031
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (await FrameCodec.ReadAsync(_stream, ct).ConfigureAwait(false) is { } f)
            {
                if (!Enum.IsDefined((MessageType)f.Type))
                {
                    _log($"知らない type 0x{f.Type:X2} の枠を読み飛ばした");
                    continue;
                }

                var type = (MessageType)f.Type;
                if (!_machine.Accepts(type))
                {
                    _log($"配信の間に受けない {type} が来たので閉じる");
                    return;
                }

                switch (type)
                {
                    case MessageType.Pong:
                        var (t1, t2) = Messages.ParsePong(f.Payload.Span);
                        var now = _clock.NowUs;
                        Latency.AddPong(t1, t2, now);
                        Volatile.Write(ref _lastPongUs, now);
                        break;
                    case MessageType.Bye:
                        _log($"端末から Bye を受けた: {ReasonOf(f)}");
                        return;
                    default:
                        _incoming.Writer.TryWrite(f);
                        break;
                }
            }

            _log("端末が接続を閉じた");
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or FormatException or FrameTooLargeException or OperationCanceledException or ObjectDisposedException)
        {
            if (!ct.IsCancellationRequested)
            {
                _log($"受信を止めた: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private async Task PingLoopAsync(CancellationToken ct)
    {
        var timeoutUs = (long)_options.PongTimeout.TotalMicroseconds;
        try
        {
            while (true)
            {
                await Task.Delay(_options.PingInterval, ct).ConfigureAwait(false);
                if (_clock.NowUs - Volatile.Read(ref _lastPongUs) > timeoutUs)
                {
                    _log($"{_options.PongTimeout.TotalSeconds:0} 秒 Pong が来ないので閉じる");
                    return;
                }

                // 書き込みが詰まって Pong の時間切れまで送れなければ、それも切れたと見る
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                limit.CancelAfter(_options.PongTimeout);
                await SendAsync(MessageType.Ping, Messages.Ping(_clock.NowUs), limit.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            if (!ct.IsCancellationRequested)
            {
                _log($"Ping を送れないので閉じる: {ex.GetType().Name}");
            }
        }
    }

    private static string ReasonOf(Frame f)
    {
        try
        {
            return Messages.FromJson<ReasonMsg>(f.Payload.Span).Reason;
        }
        catch (FormatException)
        {
            return "(読めない)";
        }
    }
}
