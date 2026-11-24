// ペアリングの 6 桁の計算と、ペアリングした端末の台帳(10 台まで。トークンは SHA-256 だけを持つ)。
// 台帳の読み書き(devices.json)は Link がする。ここは表と照合だけ。
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TabSpan.Core;

public static class PairingCode
{
    /// <summary>SHA-256(証明書の DER ‖ deviceId の UTF-8 ‖ clientNonce ‖ serverNonce) の先頭 4 バイト(BE)を 1,000,000 で割った余りを 6 桁で。</summary>
    public static string Compute(ReadOnlySpan<byte> certDer, string deviceId, ReadOnlySpan<byte> clientNonce, ReadOnlySpan<byte> serverNonce)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        h.AppendData(certDer);
        h.AppendData(Encoding.UTF8.GetBytes(deviceId));
        h.AppendData(clientNonce);
        h.AppendData(serverNonce);
        var hash = h.GetHashAndReset();
        return (BinaryPrimitives.ReadUInt32BigEndian(hash) % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }
}

/// <summary>devices.json の 1 行。TokenSha256 は SHA-256(token) の小文字の 16 進(64 文字)。</summary>
public sealed record DeviceEntry(string DeviceId, string Name, string TokenSha256, DateTimeOffset PairedAt, DateTimeOffset LastSeenAt);

/// <summary>端末の台帳。複数の接続から呼ばれてよい(中で lock する)。</summary>
public sealed class DeviceRegistry
{
    public const int MaxDevices = 10;

    private readonly List<DeviceEntry> _entries;
    private readonly Lock _lock = new();

    public DeviceRegistry(IEnumerable<DeviceEntry> entries)
    {
        _entries = [.. entries];
    }

    /// <summary>今の行の写し。</summary>
    public IReadOnlyList<DeviceEntry> Entries
    {
        get
        {
            lock (_lock)
            {
                return [.. _entries];
            }
        }
    }

    public bool IsFull
    {
        get
        {
            lock (_lock)
            {
                return _entries.Count >= MaxDevices;
            }
        }
    }

    /// <summary>同じ deviceId の行は置き換える(満杯でも通る)。新しい deviceId で満杯なら InvalidOperationException。</summary>
    public void Upsert(string deviceId, string name, ReadOnlySpan<byte> token, DateTimeOffset now)
    {
        var entry = new DeviceEntry(deviceId, name, Convert.ToHexStringLower(SHA256.HashData(token)), now, now);
        lock (_lock)
        {
            var i = _entries.FindIndex(e => e.DeviceId == deviceId);
            if (i >= 0)
            {
                _entries[i] = entry;
                return;
            }

            if (_entries.Count >= MaxDevices)
            {
                throw new InvalidOperationException($"登録できる端末は {MaxDevices} 台まで");
            }

            _entries.Add(entry);
        }
    }

    /// <summary>トークンを照合する(FixedTimeEquals)。合えば LastSeenAt を now にする。</summary>
    public bool Verify(string deviceId, ReadOnlySpan<byte> token, DateTimeOffset now)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(token, hash);
        lock (_lock)
        {
            var i = _entries.FindIndex(e => e.DeviceId == deviceId);
            if (i < 0)
            {
                return false;
            }

            Span<byte> stored = stackalloc byte[32];
            if (Convert.FromHexString(_entries[i].TokenSha256, stored, out _, out var written) != System.Buffers.OperationStatus.Done || written != 32
                || !CryptographicOperations.FixedTimeEquals(hash, stored))
            {
                return false;
            }

            _entries[i] = _entries[i] with { LastSeenAt = now };
            return true;
        }
    }

    public bool Remove(string deviceId)
    {
        lock (_lock)
        {
            return _entries.RemoveAll(e => e.DeviceId == deviceId) > 0;
        }
    }
}
