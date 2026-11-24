// TLS の待ち受け(lanEnabled が偽なら 127.0.0.1 だけ。INV-3・FR-2)と LAN の探索(FR-5)、認証までのやり取り: Hello の照合 → HelloAck、ペアリング(§9.4、INV-6)、遮断(FR-3)、2 本目の busy(FR-8)。
// 認証が済んだ接続だけを LinkSession にして外の処理(映像)に渡す。それより前は HelloAck・Pair*・Bye しか送らない(INV-4)。
// 映像の型は参照しない(INV-14)。外の処理は onSession で受け、LinkSession.SendAsync で枠を差し込む。
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using TabSpan.Core;

namespace TabSpan.Link;

public sealed record LinkOptions
{
    public int Port { get; init; } = 47815;

    /// <summary>FR-2: 真なら 0.0.0.0 で待ち、UDP の探索に返事をする。</summary>
    public bool LanEnabled { get; init; }

    /// <summary>LanEnabled のとき待つアドレス。テストだけが 127.0.0.1 に変える(0.0.0.0 で待たないため)。</summary>
    internal IPAddress LanBindAddress { get; init; } = IPAddress.Any;
    public string PcName { get; init; } = Environment.MachineName;
    public string AppVersion { get; init; } = "0.1.0";

    /// <summary>HelloAck の touchEnabled(送る時の値を読む)。</summary>
    public Func<bool> TouchEnabled { get; init; } = () => false;
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan PongTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan FirstFrameTimeout { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>ペアリングの要求。Code は両方の画面に出す 6 桁。</summary>
public sealed record PairingPrompt(string DeviceId, string DeviceName, string Code);

public sealed class LinkServer : IAsyncDisposable
{
    /// <summary>INV-5: TLS 1.2 と 1.3 だけ。</summary>
    public const SslProtocols AllowedProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;

    public static readonly TimeSpan PairingWindowLength = TimeSpan.FromSeconds(120);

    private readonly List<Identity> _createdIdentities = []; // 作り直しで作った物(閉じるときに Dispose する)
    private readonly SemaphoreSlim _gate = new(1, 1); // 待ち受けの作り直しと鍵の作り直しを 1 つずつ
    private LinkOptions _options;
    private Identity _identity;
    private readonly DeviceRegistry _registry;
    private readonly Action _saveRegistry;
    private readonly AuthThrottle _throttle;
    private readonly IClock _clock;
    private readonly Action<string> _log;
    private readonly Func<LinkSession, CancellationToken, Task> _onSession;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _connections = [];
    private TcpListener? _listener;
    private Task? _acceptLoop;
    private DiscoveryResponder? _discovery;
    private LinkSession? _active;
    private PairingWindow? _pairing;

    public LinkServer(LinkOptions options, Identity identity, DeviceRegistry registry, Action saveRegistry, AuthThrottle throttle, IClock clock,
        Action<string> log, Func<LinkSession, CancellationToken, Task> onSession)
    {
        _options = options;
        _identity = identity;
        _registry = registry;
        _saveRegistry = saveRegistry;
        _throttle = throttle;
        _clock = clock;
        _log = log;
        _onSession = onSession;
    }

    /// <summary>つないでいる(認証の済んだ)端末。無ければ null。</summary>
    public LinkSession? Active => Volatile.Read(ref _active);

    /// <summary>実際に待ち受けているポート(0 を渡したテスト用)。</summary>
    public int Port => (_listener?.LocalEndpoint as IPEndPoint)?.Port ?? 0;

    /// <summary>今の鍵(ResetIdentityAsync で替わる)。</summary>
    public Identity Identity => Volatile.Read(ref _identity);

    /// <summary>探索の UDP の待ち場所(LAN のときだけ。テスト用)。</summary>
    public IPEndPoint? DiscoveryEndPoint => _discovery?.LocalEndPoint;

    /// <summary>セッションの始まりと終わり(画面の更新用)。</summary>
    public event Action? Changed;

    /// <summary>ListenPlanner の計画どおりに待ち受けを始める。ポートが使えなければ SocketException(呼ぶ側が画面に出す)。</summary>
    public void Start()
    {
        _gate.Wait();
        try
        {
            Listen();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// FR-2: 待ち受けを作り直す。ポートを変えたら今のセッションを、LAN を切ったら LAN から来ているセッションを Bye(user) で閉じる。
    /// Start が失敗した後(ポートが使えない)でも呼んでよい。新しいポートが使えなければ SocketException(待ち受けは止まったまま)。
    /// </summary>
    public async Task ReconfigureAsync(int port, bool lanEnabled)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var portChanged = port != _options.Port;
            await StopListeningAsync().ConfigureAwait(false);
            _options = _options with { Port = port, LanEnabled = lanEnabled };
            if (Active is { } active && (portChanged || (!lanEnabled && !active.IsUsb)))
            {
                await active.CloseAsync("user").ConfigureAwait(false);
            }

            Listen();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>FR-4: 台帳から消して保存し、その端末がつないでいれば Bye(unpaired) で閉じる。</summary>
    public async Task UnpairAsync(string deviceId)
    {
        if (_registry.Remove(deviceId))
        {
            _saveRegistry();
            _log("端末のペアリングを解除した");
        }

        if (Active is { } active && active.Hello.DeviceId == deviceId)
        {
            await active.CloseAsync("unpaired").ConfigureAwait(false);
        }
    }

    /// <summary>§10: 鍵を作り直して台帳を空にし、つないでいる端末を Bye(unpaired) で閉じ、新しい鍵で待ち受け直す。</summary>
    public async Task ResetIdentityAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Active is { } active)
            {
                await active.CloseAsync("unpaired").ConfigureAwait(false);
            }

            foreach (var e in _registry.Entries)
            {
                _registry.Remove(e.DeviceId);
            }

            _saveRegistry();
            await StopListeningAsync().ConfigureAwait(false);
            var fresh = Identity.Recreate(Identity.FilePath, _clock);
            _createdIdentities.Add(fresh);
            Volatile.Write(ref _identity, fresh);
            _log("鍵を作り直し、台帳を空にした");
            Listen();
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke();
    }

    private void Listen()
    {
        var plan = ListenPlanner.For(_options.LanEnabled);
        var address = plan.Udp ? _options.LanBindAddress : plan.TcpAddress;
        var listener = new TcpListener(address, _options.Port);
        listener.Start();
        if (plan.Udp)
        {
            var discovery = new DiscoveryResponder(new IPEndPoint(address, ((IPEndPoint)listener.LocalEndpoint).Port),
                id => _registry.Entries.Any(e => e.DeviceId == id), Identity.PcInstanceId, () => Port, _log);
            try
            {
                discovery.Start();
            }
            catch (SocketException)
            {
                listener.Stop();
                throw;
            }

            _discovery = discovery;
        }

        _listener = listener;
        _log($"{address}:{Port} で待ち受けを始めた{(plan.Udp ? "(UDP の探索にも返事をする)" : "")}");
        _acceptLoop = AcceptLoopAsync(listener);
    }

    /// <summary>待ち受けと探索だけを止める。つないでいる接続は残す。</summary>
    private async Task StopListeningAsync()
    {
        _listener?.Stop();
        _listener = null;
        if (Interlocked.Exchange(ref _discovery, null) is { } discovery)
        {
            await discovery.DisposeAsync().ConfigureAwait(false);
        }

        await (_acceptLoop ?? Task.CompletedTask).ConfigureAwait(false);
        _acceptLoop = null;
    }

    /// <summary>
    /// ペアリングの受け付けを開く(既定 120 秒)。要求が来たら decide を呼び、真なら許可。閉じる(Dispose)と受け付けを止め、処理中の要求は拒否になる。
    /// decide に渡す token は、時間切れ・閉じた・端末が切れたときに取り消される。
    /// </summary>
    public IDisposable OpenPairing(Func<PairingPrompt, CancellationToken, Task<bool>> decide, TimeSpan? length = null)
    {
        var window = new PairingWindow(decide, length ?? PairingWindowLength);
        Interlocked.Exchange(ref _pairing, window)?.Dispose();
        return window;
    }

    private async Task AcceptLoopAsync(TcpListener listener)
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                break;
            }

            lock (_connections)
            {
                _connections.RemoveAll(t => t.IsCompleted);
                _connections.Add(HandleClientAsync(client));
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true; // D-12
                var remote = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;
                var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
                await using (tls.ConfigureAwait(false))
                {
                    using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
                    {
                        handshake.CancelAfter(_options.HandshakeTimeout);
                        await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                        {
                            ServerCertificate = Identity.Certificate,
                            EnabledSslProtocols = AllowedProtocols,
                            ClientCertificateRequired = false,
                        }, handshake.Token).ConfigureAwait(false);
                    }

                    await ServeAsync(tls, remote, _stop.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is AuthenticationException or IOException or OperationCanceledException or ObjectDisposedException or SocketException)
            {
                _log($"接続を閉じた: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>TLS の済んだ 1 本の接続を扱う(テストは平文の流れと好きな remote で呼ぶ)。戻ったら接続は閉じてよい。</summary>
    public async Task ServeAsync(Stream stream, IPAddress remote, CancellationToken ct)
    {
        var key = remote.ToString();
        var writer = new FrameWriter(stream);
        if (_throttle.IsBlocked(key))
        {
            // FR-3: TLS の確立の直後に閉じる。端末が理由を出せるように Bye(blocked) だけ送る
            _log("認証の失敗が続いたアドレスからの接続を断った");
            await writer.TrySendAsync(MessageType.Bye, Messages.ToJson(new ReasonMsg("blocked")), ct).ConfigureAwait(false);
            return;
        }

        var machine = new SessionMachine();
        try
        {
            while (true)
            {
                Frame? frame;
                using (var first = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    first.CancelAfter(_options.FirstFrameTimeout);
                    frame = await FrameCodec.ReadAsync(stream, first.Token).ConfigureAwait(false);
                }

                if (frame is not { } f)
                {
                    return;
                }

                if (!Enum.IsDefined((MessageType)f.Type))
                {
                    _log($"知らない type 0x{f.Type:X2} の枠を読み飛ばした");
                    continue;
                }

                var type = (MessageType)f.Type;
                if (!machine.Accepts(type))
                {
                    _log($"認証の前に {type} が来たので閉じる");
                    _throttle.Fail(key);
                    return;
                }

                switch (type)
                {
                    case MessageType.Hello:
                        await HelloAsync(stream, writer, remote, machine, Messages.FromJson<HelloMsg>(f.Payload.Span), ct).ConfigureAwait(false);
                        return;
                    case MessageType.PairRequest:
                        await PairAsync(stream, writer, remote, machine, Messages.FromJson<PairRequestMsg>(f.Payload.Span), ct).ConfigureAwait(false);
                        return;
                    default: // Bye
                        return;
                }
            }
        }
        catch (Exception ex) when (ex is FormatException or FrameTooLargeException or EndOfStreamException)
        {
            // INV-15・§10: 中身を読まずに閉じる。認証の前なので、認証の失敗として数える
            _log($"認証の前に読めない枠が来たので閉じる: {ex.GetType().Name}: {ex.Message}");
            _throttle.Fail(key);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            _log($"認証の前に切れた: {ex.GetType().Name}");
        }
    }

    private async Task HelloAsync(Stream stream, FrameWriter writer, IPAddress remote, SessionMachine machine, HelloMsg hello, CancellationToken ct)
    {
        var key = remote.ToString();
        if (hello.ProtocolVersion != Messages.ProtocolVersion)
        {
            _log($"プロトコルの版が合わない(端末 {hello.ProtocolVersion}、PC {Messages.ProtocolVersion})");
            await writer.TrySendAsync(MessageType.HelloAck, Ack(false, "version"), ct).ConfigureAwait(false);
            return;
        }

        var token = DecodeBase64(hello.Token);
        if (token is null || !_registry.Verify(hello.DeviceId, token, _clock.UtcNow))
        {
            _throttle.Fail(key);
            _log($"認証に失敗した(端末 {hello.DeviceName})");
            await writer.TrySendAsync(MessageType.HelloAck, Ack(false, "auth"), ct).ConfigureAwait(false);
            return;
        }

        CryptographicOperations.ZeroMemory(token);
        _throttle.Succeed(key);
        _saveRegistry(); // LastSeenAt

        var session = new LinkSession(stream, writer, remote, hello, machine, _options, _clock, _log);
        if (Interlocked.CompareExchange(ref _active, session, null) is not null)
        {
            _log($"別の端末がつないでいるので断った(端末 {hello.DeviceName})");
            await writer.TrySendAsync(MessageType.HelloAck, Ack(false, "busy"), ct).ConfigureAwait(false);
            return;
        }

        try
        {
            machine.Authenticate();
            await writer.SendAsync(MessageType.HelloAck, Ack(true, null), ct).ConfigureAwait(false);
            _log($"{hello.DeviceName} がつながった({(session.IsUsb ? "USB" : "LAN")})");
            Changed?.Invoke();
            await session.RunAsync(_onSession, ct).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _active, null);
            _log($"{hello.DeviceName} が切れた");
            Changed?.Invoke();
        }
    }

    private async Task PairAsync(Stream stream, FrameWriter writer, IPAddress remote, SessionMachine machine, PairRequestMsg request, CancellationToken ct)
    {
        async Task Reject(string reason)
        {
            _log($"ペアリングを断った({reason})");
            await writer.TrySendAsync(MessageType.PairReject, Messages.ToJson(new ReasonMsg(reason)), ct).ConfigureAwait(false);
        }

        if (request.ProtocolVersion != Messages.ProtocolVersion)
        {
            await Reject("version").ConfigureAwait(false);
            return;
        }

        if (!IPAddress.IsLoopback(remote)) // INV-6
        {
            await Reject("usb_only").ConfigureAwait(false);
            return;
        }

        var window = Volatile.Read(ref _pairing);
        if (window is null || !window.IsOpen)
        {
            await Reject("not_accepting").ConfigureAwait(false);
            return;
        }

        if (!window.TryBegin())
        {
            await Reject("busy").ConfigureAwait(false);
            return;
        }

        try
        {
            if (_registry.IsFull && _registry.Entries.All(e => e.DeviceId != request.DeviceId))
            {
                await Reject("full").ConfigureAwait(false);
                return;
            }

            var clientNonce = DecodeBase64(request.ClientNonce) is { Length: 16 } n ? n : throw new FormatException("clientNonce が 16 バイトでない");
            machine.BeginPairing();
            var identity = Identity;
            var serverNonce = RandomNumberGenerator.GetBytes(16);
            await writer.SendAsync(MessageType.PairChallenge,
                Messages.ToJson(new PairChallengeMsg(identity.PcInstanceId, _options.PcName, Convert.ToBase64String(serverNonce))), ct).ConfigureAwait(false);
            var prompt = new PairingPrompt(request.DeviceId, request.DeviceName, PairingCode.Compute(identity.CertDer, request.DeviceId, clientNonce, serverNonce));
            _log($"ペアリングの要求: {request.DeviceName}");

            // 決めるのを待つ間に端末が切れたら(Bye や切断)、決めるのを取り消す
            using var decision = CancellationTokenSource.CreateLinkedTokenSource(ct, window.Token);
            var watch = WatchForCloseAsync(stream, decision);
            bool approved;
            try
            {
                approved = await window.Decide(prompt, decision.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                approved = false;
            }

            var lost = watch.IsCompletedSuccessfully && watch.Result; // 時間切れ・閉じたことで読みを取り消しただけなら、端末はまだいる
            if (!approved || decision.IsCancellationRequested)
            {
                if (!lost)
                {
                    await Reject("denied").ConfigureAwait(false);
                }

                return;
            }

            var token = RandomNumberGenerator.GetBytes(32);
            try
            {
                _registry.Upsert(request.DeviceId, request.DeviceName, token, _clock.UtcNow);
                _saveRegistry();
                await writer.SendAsync(MessageType.PairAccept, Messages.ToJson(new PairAcceptMsg(Convert.ToBase64String(token))), ct).ConfigureAwait(false);
                _log($"ペアリングした: {request.DeviceName}");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(token);
            }
        }
        finally
        {
            window.End();
        }
    }

    /// <summary>ペアリングの間に端末から何か来るか切れたら、決めるのを取り消す。端末の側で終わったなら真、こちらが読みを取り消したなら偽。</summary>
    private static async Task<bool> WatchForCloseAsync(Stream stream, CancellationTokenSource decision)
    {
        var lost = true;
        try
        {
            await FrameCodec.ReadAsync(stream, decision.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or FormatException or FrameTooLargeException or EndOfStreamException)
        {
            // 切れた・読めない物が来た
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            lost = false; // 時間切れ・受け付けを閉じた・サーバーを止めた
        }

        try
        {
            await decision.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // ペアリングの処理はもう終わっている(接続を閉じたので読みが終わった)
        }

        return lost;
    }

    private byte[] Ack(bool ok, string? reason) =>
        Messages.ToJson(new HelloAckMsg(ok, reason, Identity.PcInstanceId, _options.PcName, _options.AppVersion, _options.TouchEnabled()));

    private static byte[]? DecodeBase64(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var buffer = new byte[text.Length];
        return Convert.TryFromBase64String(text, buffer, out var written) ? buffer[..written] : null;
    }

    /// <summary>つないでいる端末に Bye(user) を送って切る(画面の「切断」)。</summary>
    public Task DisconnectAsync() => Active?.CloseAsync("user") ?? Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _listener?.Stop();
        if (Interlocked.Exchange(ref _discovery, null) is { } discovery)
        {
            await discovery.DisposeAsync().ConfigureAwait(false);
        }

        Interlocked.Exchange(ref _pairing, null)?.Dispose();
        if (Active is { } active)
        {
            await active.CloseAsync("shutdown").ConfigureAwait(false);
        }

        Task[] pending;
        lock (_connections)
        {
            pending = [.. _connections];
        }

        try
        {
            await Task.WhenAll([.. pending, _acceptLoop ?? Task.CompletedTask]).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // 終わらない接続があっても、終了は止めない(_stop は取り消し済みなので、残りはそのうち止まる)
            _log("5 秒で閉じ終わらない接続を残して終わる");
        }

        foreach (var created in _createdIdentities)
        {
            created.Dispose();
        }
    }

    private sealed class PairingWindow(Func<PairingPrompt, CancellationToken, Task<bool>> decide, TimeSpan length) : IDisposable
    {
        private readonly CancellationTokenSource _open = new(length);
        private int _busy;

        public bool IsOpen => !_open.IsCancellationRequested;

        public CancellationToken Token => _open.Token;

        public Task<bool> Decide(PairingPrompt prompt, CancellationToken ct) => decide(prompt, ct);

        public bool TryBegin() => Interlocked.CompareExchange(ref _busy, 1, 0) == 0;

        public void End() => Volatile.Write(ref _busy, 0);

        // CTS は Dispose しない(処理中の要求が Token を持っている)。取り消すだけ
        public void Dispose() => _open.Cancel();
    }
}
