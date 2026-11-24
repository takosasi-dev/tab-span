// PC の身元: ECDSA P-256 の自己署名証明書(10 年)と pcInstanceId。identity.bin に DPAPI(現在のユーザー)で暗号化して置く(仕様 §9.4、INV-11)。
// SChannel はメモリ上だけの鍵をサーバー側で使えないので、PFX から読み直した証明書を SslStream に渡す。
// 読めない identity.bin は *.broken-<時刻> に残して作り直す(呼ぶ側は台帳を空にする。§10)。
using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using TabSpan.Core;

namespace TabSpan.Link;

public sealed class Identity : IDisposable
{
    private static readonly byte[] Magic = "TSI1"u8.ToArray();

    private Identity(string pcInstanceId, X509Certificate2 certificate)
    {
        PcInstanceId = pcInstanceId;
        Certificate = certificate;
        CertDer = certificate.RawData;
    }

    public string PcInstanceId { get; }

    public X509Certificate2 Certificate { get; }

    /// <summary>証明書の DER。ペアリングの 6 桁の計算に使う。</summary>
    public byte[] CertDer { get; }

    /// <summary>端末が覚える指紋(DER の SHA-256)の 16 進。ログに出す時は先頭だけにする。</summary>
    public string Fingerprint => Convert.ToHexString(SHA256.HashData(CertDer));

    /// <summary>読む。無ければ作る。Created が真なら新しく作った(台帳を空にする)。BrokenCopy は読めずに残したファイル。</summary>
    public static (Identity Identity, bool Created, string? BrokenCopy) LoadOrCreate(string path, IClock clock)
    {
        string? broken = null;
        if (File.Exists(path))
        {
            var loaded = TryLoad(File.ReadAllBytes(path));
            if (loaded is not null)
            {
                return (loaded, false, null);
            }

            broken = AtomicFile.SetAside(path, clock.UtcNow);
        }

        var pcInstanceId = Guid.NewGuid().ToString();
        var pfx = CreatePfx(clock.UtcNow);
        var plain = new byte[Magic.Length + 4 + 36 + pfx.Length];
        try
        {
            Magic.CopyTo(plain, 0);
            BinaryPrimitives.WriteInt32BigEndian(plain.AsSpan(4), 36);
            System.Text.Encoding.ASCII.GetBytes(pcInstanceId, plain.AsSpan(8));
            pfx.CopyTo(plain, 8 + 36);
            AtomicFile.Write(path, ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            CryptographicOperations.ZeroMemory(pfx);
        }

        return (TryLoad(File.ReadAllBytes(path)) ?? throw new InvalidOperationException("作った identity.bin を読み直せない"), true, broken);
    }

    private static Identity? TryLoad(byte[] protectedBytes)
    {
        byte[] plain;
        try
        {
            plain = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException)
        {
            return null; // 別のユーザー・別の PC で暗号化された、または壊れている
        }

        try
        {
            if (plain.Length < 8 || !plain.AsSpan(0, 4).SequenceEqual(Magic))
            {
                return null;
            }

            var idLength = BinaryPrimitives.ReadInt32BigEndian(plain.AsSpan(4));
            if (idLength is <= 0 or > 64 || plain.Length < 8 + idLength)
            {
                return null;
            }

            var id = System.Text.Encoding.ASCII.GetString(plain, 8, idLength);
            var certificate = X509CertificateLoader.LoadPkcs12(plain.AsSpan(8 + idLength), password: null);
            return Guid.TryParse(id, out _) && certificate.HasPrivateKey ? new Identity(id, certificate) : null;
        }
        catch (CryptographicException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private static byte[] CreatePfx(DateTimeOffset now)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=TabSpan", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false)); // サーバーの認証
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        using var certificate = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(10));
        return certificate.Export(X509ContentType.Pfx);
    }

    public void Dispose() => Certificate.Dispose();
}
