// 自己署名の証明書(ECDSA P-256)で、127.0.0.1 に TLS 1.2 以上の待ち受けを 60 秒開き、つながった相手ごとに TLS の版と暗号スイートを出す(仕様書 §8.4、§12 Phase 0)。
// 待ち受けはループバックだけ。握手の後は何も送らず、何も読まずに閉じる。
// Windows の SChannel は一時鍵のままの証明書をサーバー側で使えない(Microsoft Learn「Troubleshoot SslStream authentication issues」)ので、PFX に出して読み直す。
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace TabSpan.Probe;

internal static class TlsProbe
{
    private const int ListenMs = 60_000;
    private const int HandshakeMs = 10_000;

    internal static int Run(string portText)
    {
        if (!int.TryParse(portText, out var port) || port is < 1 or > 65535)
        {
            Console.WriteLine("失敗: port は 1〜65535 の整数にしてください");
            return 2;
        }

        X509Certificate2 certificate = null!;
        if (!Attempt.Step("自己署名の証明書を作る (ECDSA P-256、PFX に出して読み直す)", () => certificate = CreateCertificate()))
        {
            return 1;
        }

        using (certificate)
        {
            Console.WriteLine($"証明書の SHA-256 の指紋: {certificate.GetCertHashString(HashAlgorithmName.SHA256)}");
            Console.WriteLine("(読み直した鍵は、利用者の鍵の置き場に一時的に置かれ、この Probe が証明書を手放すと消えます)");

            var listener = new TcpListener(IPAddress.Loopback, port);
            if (!Attempt.Step($"127.0.0.1:{port} で待ち受けを開く", listener.Start))
            {
                return 1;
            }

            Console.WriteLine($"{ListenMs / 1000} 秒待ち受けます。端末からつなぐ前に、adb reverse tcp:{port} tcp:{port} を手で実行してください。");
            var connections = 0;
            var clock = Stopwatch.StartNew();
            try
            {
                while (clock.ElapsedMilliseconds < ListenMs)
                {
                    // 負の値を渡すと「時間切れなし」になるので、1 ms を下限にする
                    using var remaining = new CancellationTokenSource(TimeSpan.FromMilliseconds(Math.Max(1, ListenMs - clock.ElapsedMilliseconds)));
                    TcpClient client;
                    try
                    {
                        client = listener.AcceptTcpClientAsync(remaining.Token).AsTask().GetAwaiter().GetResult();
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    Console.WriteLine($"接続 {++connections} ({clock.ElapsedMilliseconds / 1000} 秒後): {Attempt.Read(() => Handshake(client, certificate))}");
                }
            }
            finally
            {
                listener.Stop();
            }

            Console.WriteLine($"{ListenMs / 1000} 秒たったので閉じました。つながった数: {connections}");
            return 0;
        }
    }

    private static string Handshake(TcpClient client, X509Certificate2 certificate)
    {
        using (client)
        using (var stream = new SslStream(client.GetStream(), leaveInnerStreamOpen: false))
        using (var timeout = new CancellationTokenSource(HandshakeMs))
        {
            var options = new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ClientCertificateRequired = false,
            };
            stream.AuthenticateAsServerAsync(options, timeout.Token).GetAwaiter().GetResult();
            return $"TLS の版 = {stream.SslProtocol}、暗号スイート = {stream.NegotiatedCipherSuite}";
        }
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=TabSpan.Probe", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false)); // サーバーの認証
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());

        // 証明書の有効期間には壁時計が要る(時間を測る用途ではない)
        var now = TimeProvider.System.GetUtcNow();
        using var ephemeral = request.CreateSelfSigned(now.AddDays(-1), now.AddDays(7));
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), password: null);
    }
}
