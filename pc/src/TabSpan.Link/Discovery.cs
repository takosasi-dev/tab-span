// LAN の探索(§9.5・FR-5・D-11): 「TABSPAN-DISCOVER 1 <deviceId>」に、台帳にある端末からだけ「TABSPAN-HERE 1 <pcInstanceId> <tcpPort>」を返す。
// 512 バイトを超える・形が合わない・台帳に無い問い合わせには黙る。PC からは同報しない(返事は送り元へのユニキャストだけ)。
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace TabSpan.Link;

public static class DiscoveryProtocol
{
    public const int MaxDatagram = 512;

    /// <summary>返す ASCII のバイト列。返事をしないなら null。行の末尾の改行(\n・\r\n)は許す。</summary>
    public static byte[]? Reply(ReadOnlySpan<byte> datagram, Func<string, bool> isPaired, string pcInstanceId, int tcpPort)
    {
        if (datagram.Length > MaxDatagram)
        {
            return null;
        }

        var line = datagram.TrimEnd("\r\n"u8);
        foreach (var b in line)
        {
            if (b is < 0x20 or > 0x7E)
            {
                return null;
            }
        }

        var parts = Encoding.ASCII.GetString(line).Split(' ');
        if (parts is not ["TABSPAN-DISCOVER", "1", { Length: > 0 } deviceId] || !isPaired(deviceId))
        {
            return null;
        }

        return Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"TABSPAN-HERE 1 {pcInstanceId} {tcpPort}"));
    }
}

/// <summary>UDP で問い合わせを待ち、Reply が null でなければ送り元へ返す。</summary>
public sealed class DiscoveryResponder(IPEndPoint bind, Func<string, bool> isPaired, string pcInstanceId, Func<int> tcpPort, Action<string> log) : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private UdpClient? _udp;
    private Task _loop = Task.CompletedTask;

    /// <summary>実際に待っている場所(port 0 を渡したテスト用)。Start の前は null。</summary>
    public IPEndPoint? LocalEndPoint => _udp?.Client.LocalEndPoint as IPEndPoint;

    /// <summary>待ち始める。場所を使えなければ SocketException。</summary>
    public void Start()
    {
        _udp = new UdpClient(bind);
        _loop = LoopAsync(_udp, _stop.Token);
    }

    private async Task LoopAsync(UdpClient udp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await udp.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.MessageSize)
            {
                continue; // 前の返事の宛先が閉じていた(ICMP)・大きすぎた。どちらも待ち続ける
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                break;
            }

            if (DiscoveryProtocol.Reply(received.Buffer, isPaired, pcInstanceId, tcpPort()) is not { } reply)
            {
                continue;
            }

            try
            {
                await udp.SendAsync(reply, received.RemoteEndPoint, ct).ConfigureAwait(false);
                log("探索の問い合わせに返事をした");
            }
            catch (SocketException ex)
            {
                log($"探索の返事を送れない: {ex.SocketErrorCode}");
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                break;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _udp?.Dispose();
        await _loop.ConfigureAwait(false);
        _stop.Dispose();
    }
}
