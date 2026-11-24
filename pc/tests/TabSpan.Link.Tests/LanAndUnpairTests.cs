// Phase 4 の Link: 探索の返事(AC-10)、待ち受けの作り直し(FR-2)、解除(FR-4・AC-9)、鍵の作り直し(§10)。
// 0.0.0.0 では待たない。LAN の待ち受けは内部の LanBindAddress を 127.0.0.1 にし、LAN のセッションは ServeAsync に LAN のアドレスを渡して作る。
using System.Net;
using System.Net.Sockets;
using System.Text;
using TabSpan.Core;

namespace TabSpan.Link.Tests;

public sealed class LanAndUnpairTests
{
    private const string PcId = "0b5c3f9e-1111-4222-8333-444455556666";

    private static string? Reply(string text, Func<string, bool>? isPaired = null) =>
        DiscoveryProtocol.Reply(Encoding.ASCII.GetBytes(text), isPaired ?? (id => id == "dev-1"), PcId, 47815) is { } b ? Encoding.ASCII.GetString(b) : null;

    [Fact]
    public void AC10_台帳にある端末だけに返事をし_形が合わない行と_513_バイトには黙る()
    {
        Assert.Equal($"TABSPAN-HERE 1 {PcId} 47815", Reply("TABSPAN-DISCOVER 1 dev-1"));
        Assert.Equal($"TABSPAN-HERE 1 {PcId} 47815", Reply("TABSPAN-DISCOVER 1 dev-1\r\n"));
        Assert.Null(Reply("TABSPAN-DISCOVER 1 dev-2")); // 台帳に無い
        foreach (var bad in new[] { "", "TABSPAN-DISCOVER 1", "TABSPAN-DISCOVER 1 ", "TABSPAN-DISCOVER 2 dev-1", "tabspan-discover 1 dev-1",
                     "TABSPAN-DISCOVER  1 dev-1", "TABSPAN-DISCOVER 1 dev-1 x", "TABSPAN-HERE 1 dev-1", "TABSPAN-DISCOVER 1 dev\t1" })
        {
            Assert.Null(Reply(bad, _ => true));
        }

        Assert.Null(DiscoveryProtocol.Reply(Encoding.UTF8.GetBytes("TABSPAN-DISCOVER 1 端末"), _ => true, PcId, 47815));

        var id512 = new string('a', DiscoveryProtocol.MaxDatagram - "TABSPAN-DISCOVER 1 ".Length);
        Assert.NotNull(Reply("TABSPAN-DISCOVER 1 " + id512, _ => true));
        Assert.Null(Reply("TABSPAN-DISCOVER 1 " + id512 + "a", _ => true)); // 513 バイト
    }

    [Fact]
    public async Task AC10_UDP_の問い合わせに送り元へ返事をし_台帳に無い端末には黙る()
    {
        var responder = new DiscoveryResponder(new IPEndPoint(IPAddress.Loopback, 0), id => id == "dev-1", PcId, () => 50000, _ => { });
        await using (responder)
        {
            responder.Start();
            using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            Assert.Equal($"TABSPAN-HERE 1 {PcId} 50000", await AskAsync(client, responder.LocalEndPoint!, "TABSPAN-DISCOVER 1 dev-1"));
            Assert.Null(await AskAsync(client, responder.LocalEndPoint!, "TABSPAN-DISCOVER 1 dev-2"));
            Assert.Null(await AskAsync(client, responder.LocalEndPoint!, new string('x', 600)));
            Assert.NotNull(await AskAsync(client, responder.LocalEndPoint!, "TABSPAN-DISCOVER 1 dev-1")); // 黙った後も待ち続ける
        }
    }

    [Fact]
    public async Task LAN_を入れると探索に返事をし_切ると探索を止める()
    {
        await using var h = Harness.Start(options: new LinkOptions { LanEnabled = true, LanBindAddress = IPAddress.Loopback });
        h.AddDevice("dev-1");
        var at = h.Server.DiscoveryEndPoint;
        Assert.NotNull(at);
        Assert.Equal(h.Server.Port, at.Port);
        using (var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        {
            Assert.Equal($"TABSPAN-HERE 1 {h.Identity.PcInstanceId} {h.Server.Port}", await AskAsync(client, at, "TABSPAN-DISCOVER 1 dev-1"));
        }

        await h.Server.ReconfigureAsync(0, lanEnabled: false);
        Assert.Null(h.Server.DiscoveryEndPoint);
        Assert.NotEqual(0, h.Server.Port);
    }

    [Fact]
    public async Task LAN_を切ると_LAN_から来ているセッションを_Bye_user_で閉じる()
    {
        await using var h = Harness.Start(options: new LinkOptions { LanEnabled = true, LanBindAddress = IPAddress.Loopback });
        var token = h.AddDevice("dev-1");
        await using var lan = await PlainLink.OpenAsync(h.Server, IPAddress.Parse("192.168.10.20"));
        await lan.SendJsonAsync(MessageType.Hello, FakeDevice.Hello("dev-1", token));
        Assert.True(lan.ReadJson<HelloAckMsg>(await lan.ReadAsync(), MessageType.HelloAck).Ok);
        await WaitUntil(() => h.Server.Active is { IsUsb: false });

        await h.Server.ReconfigureAsync(0, lanEnabled: false);
        Assert.Equal("user", await lan.ReadByeAsync());
        await WaitUntil(() => h.Server.Active is null);
    }

    [Fact]
    public async Task LAN_を切っても_USB_のセッションは残り_ポートを変えると閉じて新しいポートで待つ()
    {
        await using var h = Harness.Start();
        var token = h.AddDevice("dev-1");
        await using var d = await h.ConnectAsync();
        await d.SendJsonAsync(MessageType.Hello, FakeDevice.Hello("dev-1", token));
        Assert.True((await d.ReadJsonAsync<HelloAckMsg>(MessageType.HelloAck)).Ok);
        await WaitUntil(() => h.Server.Active is not null);
        var session = h.Server.Active;

        await h.Server.ReconfigureAsync(0, lanEnabled: false); // 同じポートで LAN だけ切る
        Assert.Same(session, h.Server.Active);

        var port = FreePort();
        await h.Server.ReconfigureAsync(port, lanEnabled: false);
        Assert.Equal("user", await ReadByeAsync(d));
        Assert.Equal(port, h.Server.Port);
        await using var again = await h.ConnectAsync();
        await again.SendJsonAsync(MessageType.Hello, FakeDevice.Hello("dev-1", token));
        Assert.True((await again.ReadJsonAsync<HelloAckMsg>(MessageType.HelloAck)).Ok);
    }

    [Fact]
    public async Task 同じポートで待ち直しても_つないだままのセッションは残り_新しい待ち受けが受ける()
    {
        await using var h = Harness.Start();
        var port = FreePort();
        await h.Server.ReconfigureAsync(port, lanEnabled: false); // 設定の値と実際の番号をそろえる(本番の 47815 と同じ形)
        var t1 = h.AddDevice("dev-1");
        var t2 = h.AddDevice("dev-2");
        await using var d = await h.ConnectAsync();
        await d.SendJsonAsync(MessageType.Hello, FakeDevice.Hello("dev-1", t1));
        Assert.True((await d.ReadJsonAsync<HelloAckMsg>(MessageType.HelloAck)).Ok);
        await WaitUntil(() => h.Server.Active is not null);
        var session = h.Server.Active;

        await h.Server.ReconfigureAsync(port, lanEnabled: false);
        Assert.Same(session, h.Server.Active);
        Assert.Equal(port, h.Server.Port);
        await using var second = await h.ConnectAsync();
        await second.SendJsonAsync(MessageType.Hello, FakeDevice.Hello("dev-2", t2));
        var ack = await second.ReadJsonAsync<HelloAckMsg>(MessageType.HelloAck);
        Assert.Equal((false, "busy"), (ack.Ok, ack.Reason));
    }

    [Fact]
    public async Task AC9_解除するとつないでいる端末を_Bye_unpaired_で閉じ_同じトークンは_auth()
    {
        await using var h = Harness.Start();
        var token = h.AddDevice("dev-1");
        h.AddDevice("dev-2");
        await using (var d = await h.ConnectAsync())
        {
            await d.SendJsonAsync(MessageType.Hello, FakeDevice.Hello("dev-1", token));
            Assert.True((await d.ReadJsonAsync<HelloAckMsg>(MessageType.HelloAck)).Ok);
            await WaitUntil(() => h.Server.Active is not null);
            var saves = h.Saves;

            await h.Server.UnpairAsync("dev-1");
            Assert.Equal("unpaired", await ReadByeAsync(d));
            Assert.Equal(["dev-2"], h.Registry.Entries.Select(e => e.DeviceId));
            Assert.True(h.Saves > saves);
        }

        await WaitUntil(() => h.Server.Active is null);
        await using var after = await h.ConnectAsync();
        await after.SendJsonAsync(MessageType.Hello, FakeDevice.Hello("dev-1", token));
        var ack = await after.ReadJsonAsync<HelloAckMsg>(MessageType.HelloAck);
        Assert.Equal((false, "auth"), (ack.Ok, ack.Reason));
    }

    [Fact]
    public async Task 鍵を作り直すと_Bye_unpaired_で閉じ_台帳を空にし_新しい証明書で待つ()
    {
        await using var h = Harness.Start();
        var token = h.AddDevice("dev-1");
        var oldId = h.Identity.PcInstanceId;
        byte[] oldDer;
        await using (var d = await h.ConnectAsync())
        {
            oldDer = d.CertDer;
            await d.SendJsonAsync(MessageType.Hello, FakeDevice.Hello("dev-1", token));
            Assert.True((await d.ReadJsonAsync<HelloAckMsg>(MessageType.HelloAck)).Ok);
            await WaitUntil(() => h.Server.Active is not null);

            await h.Server.ResetIdentityAsync();
            Assert.Equal("unpaired", await ReadByeAsync(d));
        }

        Assert.Empty(h.Registry.Entries);
        var fresh = h.Server.Identity;
        Assert.NotEqual(oldId, fresh.PcInstanceId);
        await using (var d = await h.ConnectAsync())
        {
            Assert.NotEqual(oldDer, d.CertDer);
            Assert.Equal(fresh.CertDer, d.CertDer);
            await d.SendJsonAsync(MessageType.Hello, FakeDevice.Hello("dev-1", token));
            var ack = await d.ReadJsonAsync<HelloAckMsg>(MessageType.HelloAck);
            Assert.Equal((false, "auth", fresh.PcInstanceId), (ack.Ok, ack.Reason, ack.PcInstanceId));
        }

        var (reloaded, created, broken) = Identity.LoadOrCreate(fresh.FilePath, SystemClock.Instance);
        using (reloaded)
        {
            Assert.Equal((fresh.PcInstanceId, false, (string?)null), (reloaded.PcInstanceId, created, broken));
        }

        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(fresh.FilePath)!, "identity*")); // 古い鍵の写しを残さない
    }

    private static async Task<string?> AskAsync(UdpClient client, IPEndPoint to, string text)
    {
        await client.SendAsync(Encoding.ASCII.GetBytes(text), to);
        using var cts = new CancellationTokenSource(500);
        try
        {
            return Encoding.ASCII.GetString((await client.ReceiveAsync(cts.Token)).Buffer);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    /// <summary>Ping を読み飛ばして Bye の reason を返す。Bye の前に閉じられたら null。</summary>
    private static async Task<string?> ReadByeAsync(FakeDevice d)
    {
        while (await d.ReadAsync() is { } f)
        {
            if (f.Type == (byte)MessageType.Bye)
            {
                return Messages.FromJson<ReasonMsg>(f.Payload.Span).Reason;
            }
        }

        return null;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(50);
        }

        Assert.True(condition());
    }

    /// <summary>TLS を通さずに、好きなリモートのアドレスで ServeAsync につなぐ(LAN から来た接続の代わり)。</summary>
    private sealed class PlainLink : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly TcpClient _client;
        private readonly TcpClient _server;
        private readonly Task _serve;

        private PlainLink(TcpListener listener, TcpClient client, TcpClient server, Task serve)
        {
            (_listener, _client, _server, _serve) = (listener, client, server, serve);
        }

        public static async Task<PlainLink> OpenAsync(LinkServer server, IPAddress remote)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            var accepted = await listener.AcceptTcpClientAsync();
            return new PlainLink(listener, client, accepted, server.ServeAsync(accepted.GetStream(), remote, CancellationToken.None));
        }

        public Task SendJsonAsync<T>(MessageType type, T msg) =>
            FrameCodec.WriteAsync(_client.GetStream(), (byte)type, Messages.ToJson(msg), CancellationToken.None).AsTask();

        public async Task<Frame?> ReadAsync()
        {
            using var cts = new CancellationTokenSource(5000);
            try
            {
                return await FrameCodec.ReadAsync(_client.GetStream(), cts.Token);
            }
            catch (IOException)
            {
                return null;
            }
        }

        public T ReadJson<T>(Frame? f, MessageType expected)
        {
            Assert.Equal((byte)expected, f!.Value.Type);
            return Messages.FromJson<T>(f.Value.Payload.Span);
        }

        public async Task<string?> ReadByeAsync()
        {
            while (await ReadAsync() is { } f)
            {
                if (f.Type == (byte)MessageType.Bye)
                {
                    return Messages.FromJson<ReasonMsg>(f.Payload.Span).Reason;
                }
            }

            return null;
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await _serve;
            _server.Dispose();
            _listener.Stop();
        }
    }
}
