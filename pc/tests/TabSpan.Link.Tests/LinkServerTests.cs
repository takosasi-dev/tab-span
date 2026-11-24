// ループバックの TLS で、偽の端末とサーバーのやり取りを確かめる: ペアリング → Hello → HelloAck → Ping/Pong、
// INV-4(認証の前に映像を送らない)、INV-15、2 本目の busy、知らない type の読み飛ばし、遮断、受け付けの時間切れ、INV-6。
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using TabSpan.Core;

namespace TabSpan.Link.Tests;

public sealed class LinkServerTests
{
    [Fact]
    public async Task Pairing_then_hello_then_ping_pong()
    {
        var sessionStarted = new TaskCompletionSource<LinkSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var h = Harness.Start((s, ct) =>
        {
            sessionStarted.TrySetResult(s);
            return Task.Delay(Timeout.Infinite, ct);
        });
        string? shownOnPc = null;
        using var window = h.Server.OpenPairing((prompt, _) =>
        {
            shownOnPc = prompt.Code;
            return Task.FromResult(true);
        });

        // ペアリング
        const string deviceId = "6f1c2d4e-0000-4000-8000-000000000001";
        byte[] token;
        await using (var d = await h.ConnectAsync())
        {
            var clientNonce = RandomNumberGenerator.GetBytes(16);
            await d.SendJsonAsync(MessageType.PairRequest, new PairRequestMsg(1, deviceId, "偽の端末", Convert.ToBase64String(clientNonce)));
            var challenge = await d.ReadJsonAsync<PairChallengeMsg>(MessageType.PairChallenge);
            Assert.Equal(h.Identity.PcInstanceId, challenge.PcInstanceId);
            var shownOnDevice = PairingCode.Compute(d.CertDer, deviceId, clientNonce, Convert.FromBase64String(challenge.ServerNonce));
            token = Convert.FromBase64String((await d.ReadJsonAsync<PairAcceptMsg>(MessageType.PairAccept)).Token);
            Assert.Equal(shownOnDevice, shownOnPc);
            Assert.Equal(32, token.Length);
            Assert.Null(await d.ReadAsync()); // PC が閉じる
        }

        Assert.Single(h.Registry.Entries);
        Assert.True(h.Saves >= 1);

        // Hello → HelloAck → Ping/Pong
        await using (var d = await h.ConnectAsync())
        {
            await d.SendJsonAsync(MessageType.Hello, FakeDevice.Hello(deviceId, token));
            var ack = await d.ReadJsonAsync<HelloAckMsg>(MessageType.HelloAck);
            Assert.True(ack.Ok);
            Assert.Equal(h.Identity.PcInstanceId, ack.PcInstanceId);
            var session = await sessionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(session.IsUsb);
            Assert.Same(session, h.Server.Active);

            var ping = await d.ReadAsync();
            Assert.Equal((byte)MessageType.Ping, ping!.Value.Type);
            var t1 = Messages.ParsePing(ping.Value.Payload.Span);
            await d.SendAsync(MessageType.Pong, Messages.Pong(t1, 123));
            await WaitUntil(() => session.Latency.RttUs is not null);

            await d.SendJsonAsync(MessageType.Bye, new ReasonMsg("user"));
            await WaitUntil(() => h.Server.Active is null);
        }

        // AC-31: ログにトークンも秘密鍵も出ていない
        var all = string.Join("\n", h.Logs);
        Assert.DoesNotContain(Convert.ToBase64String(token), all, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToHexString(token), all, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Wrong_token_gets_auth_and_never_video()
    {
        var handlerCalls = 0;
        await using var h = Harness.Start(async (s, ct) =>
        {
            Interlocked.Increment(ref handlerCalls);
            await s.SendAsync(MessageType.StreamConfig, Messages.ToJson(new StreamConfigMsg(1280, 720, 60, "main", "Z0I=", "aM4=", "physical")), ct);
            await s.SendAsync(MessageType.VideoFrame, Messages.VideoFrame(0, true, 0, 0, [0, 0, 0, 1, 0x65]), ct);
        });
        h.AddDevice("dev-1");

        await using var d = await h.ConnectAsync();
        await d.SendJsonAsync(MessageType.Hello, FakeDevice.Hello("dev-1", RandomNumberGenerator.GetBytes(32)));
        var types = new List<byte>();
        while (await d.ReadAsync() is { } f)
        {
            types.Add(f.Type);
        }

        Assert.Equal([(byte)MessageType.HelloAck], types);
        Assert.Equal(0, handlerCalls);
        Assert.Null(h.Server.Active);
    }

    [Fact]
    public async Task Hello_without_token_and_unknown_device_get_auth()
    {
        await using var h = Harness.Start();
        await using var d = await h.ConnectAsync();
        await d.SendJsonAsync(MessageType.Hello, FakeDevice.Hello("nobody", null));
        var ack = await d.ReadJsonAsync<HelloAckMsg>(MessageType.HelloAck);
        Assert.Equal((false, "auth"), (ack.Ok, ack.Reason));
    }

    [Fact]
    public async Task Too_large_frame_closes_without_waiting_for_the_body()
    {
        await using var h = Harness.Start();
        await using var d = await h.ConnectAsync();
        var header = new byte[5];
        header[0] = (byte)MessageType.Hello;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(1), FrameCodec.MaxPayload + 1);
        await d.Stream.WriteAsync(header);
        await d.Stream.FlushAsync();
        Assert.Null(await d.ReadAsync(timeoutMs: 3000)); // 中身を送らないまま閉じられる
    }

    [Fact]
    public async Task Second_hello_while_streaming_gets_busy()
    {
        await using var h = Harness.Start();
        var t1 = h.AddDevice("dev-1");
        var t2 = h.AddDevice("dev-2");
        await using var first = await h.ConnectAsync();
        await first.SendJsonAsync(MessageType.Hello, FakeDevice.Hello("dev-1", t1));
        Assert.True((await first.ReadJsonAsync<HelloAckMsg>(MessageType.HelloAck)).Ok);

        await using var second = await h.ConnectAsync();
        await second.SendJsonAsync(MessageType.Hello, FakeDevice.Hello("dev-2", t2));
        var ack = await second.ReadJsonAsync<HelloAckMsg>(MessageType.HelloAck);
        Assert.Equal((false, "busy"), (ack.Ok, ack.Reason));
        Assert.Null(await second.ReadAsync());
    }

    [Fact]
    public async Task Unknown_type_is_skipped_and_version_is_checked()
    {
        await using var h = Harness.Start();
        await using var d = await h.ConnectAsync();
        await d.SendAsync((MessageType)0x7E, [1, 2, 3]);
        await d.SendJsonAsync(MessageType.Hello, FakeDevice.Hello("dev-1", null, version: 2));
        var ack = await d.ReadJsonAsync<HelloAckMsg>(MessageType.HelloAck);
        Assert.Equal((false, "version"), (ack.Ok, ack.Reason));
        Assert.Contains(h.Logs, l => l.Contains("0x7E", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pair_request_when_not_accepting()
    {
        await using var h = Harness.Start();
        await using var d = await h.ConnectAsync();
        await d.SendJsonAsync(MessageType.PairRequest, new PairRequestMsg(1, "dev-1", "偽の端末", Convert.ToBase64String(new byte[16])));
        Assert.Equal("not_accepting", (await d.ReadJsonAsync<ReasonMsg>(MessageType.PairReject)).Reason);
    }

    [Fact]
    public async Task Pairing_window_times_out_with_denied()
    {
        await using var h = Harness.Start();
        using var window = h.Server.OpenPairing((_, ct) => Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => true, TaskScheduler.Default), TimeSpan.FromMilliseconds(500));
        await using var d = await h.ConnectAsync();
        await d.SendJsonAsync(MessageType.PairRequest, new PairRequestMsg(1, "dev-1", "偽の端末", Convert.ToBase64String(new byte[16])));
        await d.ReadJsonAsync<PairChallengeMsg>(MessageType.PairChallenge);
        Assert.Equal("denied", (await d.ReadJsonAsync<ReasonMsg>(MessageType.PairReject)).Reason);
        Assert.Empty(h.Registry.Entries);
    }

    [Fact]
    public async Task Pairing_when_full_and_second_request_busy()
    {
        await using var h = Harness.Start();
        var release = new TaskCompletionSource<bool>();
        using var window = h.Server.OpenPairing((_, _) => release.Task);

        await using var first = await h.ConnectAsync();
        await first.SendJsonAsync(MessageType.PairRequest, new PairRequestMsg(1, "dev-a", "a", Convert.ToBase64String(new byte[16])));
        await first.ReadJsonAsync<PairChallengeMsg>(MessageType.PairChallenge);

        await using var second = await h.ConnectAsync();
        await second.SendJsonAsync(MessageType.PairRequest, new PairRequestMsg(1, "dev-b", "b", Convert.ToBase64String(new byte[16])));
        Assert.Equal("busy", (await second.ReadJsonAsync<ReasonMsg>(MessageType.PairReject)).Reason);
        release.SetResult(false);
        Assert.Equal("denied", (await first.ReadJsonAsync<ReasonMsg>(MessageType.PairReject)).Reason);

        for (var i = 0; i < DeviceRegistry.MaxDevices; i++)
        {
            h.AddDevice($"dev-{i}");
        }

        await using var eleventh = await h.ConnectAsync();
        await eleventh.SendJsonAsync(MessageType.PairRequest, new PairRequestMsg(1, "dev-new", "x", Convert.ToBase64String(new byte[16])));
        Assert.Equal("full", (await eleventh.ReadJsonAsync<ReasonMsg>(MessageType.PairReject)).Reason);
    }

    [Fact]
    public async Task Pair_request_from_lan_gets_usb_only()
    {
        // TLS の後の扱いだけを、平文のソケットと LAN のアドレスで確かめる(AC-5)
        await using var h = Harness.Start();
        using var window = h.Server.OpenPairing((_, _) => Task.FromResult(true));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var server = await listener.AcceptTcpClientAsync();
        var serve = h.Server.ServeAsync(server.GetStream(), IPAddress.Parse("192.168.10.20"), CancellationToken.None);

        var stream = client.GetStream();
        await FrameCodec.WriteAsync(stream, (byte)MessageType.PairRequest,
            Messages.ToJson(new PairRequestMsg(1, "dev-lan", "x", Convert.ToBase64String(new byte[16]))), CancellationToken.None);
        var f = await FrameCodec.ReadAsync(stream, CancellationToken.None);
        Assert.Equal((byte)MessageType.PairReject, f!.Value.Type);
        Assert.Equal("usb_only", Messages.FromJson<ReasonMsg>(f.Value.Payload.Span).Reason);
        await serve;
        Assert.Empty(h.Registry.Entries);
    }

    [Fact]
    public async Task Five_failures_block_the_address()
    {
        var clock = new FakeClock();
        await using var h = Harness.Start(clock: clock);
        for (var i = 0; i < AuthThrottle.MaxFailures; i++)
        {
            await using var d = await h.ConnectAsync();
            await d.SendJsonAsync(MessageType.Hello, FakeDevice.Hello("nobody", new byte[32]));
            Assert.Equal("auth", (await d.ReadJsonAsync<HelloAckMsg>(MessageType.HelloAck)).Reason);
        }

        await using (var blocked = await h.ConnectAsync())
        {
            Assert.Equal("blocked", (await blocked.ReadJsonAsync<ReasonMsg>(MessageType.Bye)).Reason);
        }

        clock.NowUs += 61_000_000;
        await using var after = await h.ConnectAsync();
        await after.SendJsonAsync(MessageType.Hello, FakeDevice.Hello("nobody", new byte[32]));
        Assert.Equal("auth", (await after.ReadJsonAsync<HelloAckMsg>(MessageType.HelloAck)).Reason);
    }

    [Fact]
    public async Task No_pong_closes_the_session()
    {
        await using var h = Harness.Start(options: new LinkOptions { PingInterval = TimeSpan.FromMilliseconds(100), PongTimeout = TimeSpan.FromMilliseconds(600) });
        var token = h.AddDevice("dev-1");
        await using var d = await h.ConnectAsync();
        await d.SendJsonAsync(MessageType.Hello, FakeDevice.Hello("dev-1", token));
        Assert.True((await d.ReadJsonAsync<HelloAckMsg>(MessageType.HelloAck)).Ok);
        while (await d.ReadAsync(timeoutMs: 5000) is { } f)
        {
            Assert.Equal((byte)MessageType.Ping, f.Type); // Pong を返さない
        }

        await WaitUntil(() => h.Server.Active is null);
    }

    [Fact]
    public async Task Incoming_frames_reach_the_handler_and_shutdown_sends_bye()
    {
        var received = new TaskCompletionSource<Frame>(TaskCreationOptions.RunContinuationsAsynchronously);
        var h = Harness.Start(async (s, ct) =>
        {
            await foreach (var f in s.Incoming.ReadAllAsync(ct))
            {
                received.TrySetResult(f);
            }
        });
        var token = h.AddDevice("dev-1");
        await using var d = await h.ConnectAsync();
        await d.SendJsonAsync(MessageType.Hello, FakeDevice.Hello("dev-1", token));
        Assert.True((await d.ReadJsonAsync<HelloAckMsg>(MessageType.HelloAck)).Ok);
        await d.SendAsync(MessageType.RequestKeyframe, []);
        Assert.Equal((byte)MessageType.RequestKeyframe, (await received.Task.WaitAsync(TimeSpan.FromSeconds(5))).Type);

        await h.DisposeAsync();
        string? bye = null;
        while (await d.ReadAsync() is { } f)
        {
            if (f.Type == (byte)MessageType.Bye)
            {
                bye = Messages.FromJson<ReasonMsg>(f.Payload.Span).Reason;
            }
        }

        Assert.Equal("shutdown", bye);
    }

    [Fact]
    public void Tls_allows_only_12_and_13() =>
        Assert.Equal(System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13, LinkServer.AllowedProtocols);

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(50);
        }

        Assert.True(condition());
    }
}
