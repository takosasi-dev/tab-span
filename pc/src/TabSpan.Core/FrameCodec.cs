// 通信の枠 [type u8][len u32 BE][payload] の符号化と読み書き。長さの上限は 4 MiB で、超えた枠は中身を読まずに例外にする。
using System.Buffers.Binary;

namespace TabSpan.Core;

public enum MessageType : byte
{
    Hello = 0x01, HelloAck = 0x02, PairRequest = 0x03, PairChallenge = 0x04, PairAccept = 0x05, PairReject = 0x06,
    StreamConfig = 0x10, VideoFrame = 0x11, RequestKeyframe = 0x12, Stats = 0x13, Touch = 0x20, Ping = 0x30, Pong = 0x31, Bye = 0x3F, Notice = 0x40,
}

public readonly record struct Frame(byte Type, ReadOnlyMemory<byte> Payload);

/// <summary>枠の長さが <see cref="FrameCodec.MaxPayload"/> を超えていた。受けた側は接続を閉じる(中身は読んでいない)。</summary>
public sealed class FrameTooLargeException : Exception
{
    public FrameTooLargeException() : base("枠の長さが上限を超えている")
    {
    }

    public FrameTooLargeException(uint length) : base($"枠の長さ {length} が上限 {FrameCodec.MaxPayload} を超えている")
    {
        Length = length;
    }

    public FrameTooLargeException(string message) : base(message)
    {
    }

    public FrameTooLargeException(string message, Exception inner) : base(message, inner)
    {
    }

    public uint Length { get; }
}

public static class FrameCodec
{
    public const int MaxPayload = 4 * 1024 * 1024; // INV-15
    public const int HeaderSize = 5;

    public static byte[] Encode(byte type, ReadOnlySpan<byte> payload)
    {
        CheckLength(payload.Length);
        var frame = new byte[HeaderSize + payload.Length];
        WriteHeader(frame, type, payload.Length);
        payload.CopyTo(frame.AsSpan(HeaderSize));
        return frame;
    }

    /// <summary>枠を 1 つ読む。最初の 1 バイトの前で終わっていれば null。ヘッダーや中身の途中で切れたら EndOfStreamException。</summary>
    public static async ValueTask<Frame?> ReadAsync(Stream s, CancellationToken ct)
    {
        var header = new byte[HeaderSize];
        var got = await s.ReadAtLeastAsync(header, HeaderSize, throwOnEndOfStream: false, ct).ConfigureAwait(false);
        if (got == 0)
        {
            return null;
        }

        if (got < HeaderSize)
        {
            throw new EndOfStreamException("枠のヘッダーの途中で切れた");
        }

        // uint のまま比べる(int にすると 0x80000000 以上が負になって上限をすり抜ける)
        var length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(1));
        if (length > MaxPayload)
        {
            throw new FrameTooLargeException(length);
        }

        var payload = new byte[length];
        await s.ReadExactlyAsync(payload, ct).ConfigureAwait(false); // 途中で切れたら EndOfStreamException
        return new Frame(header[0], payload);
    }

    public static async ValueTask WriteAsync(Stream s, byte type, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        CheckLength(payload.Length);
        var header = new byte[HeaderSize];
        WriteHeader(header, type, payload.Length);
        await s.WriteAsync(header, ct).ConfigureAwait(false);
        await s.WriteAsync(payload, ct).ConfigureAwait(false);
    }

    private static void WriteHeader(Span<byte> dest, byte type, int length)
    {
        dest[0] = type;
        BinaryPrimitives.WriteUInt32BigEndian(dest[1..], (uint)length);
    }

    private static void CheckLength(int length)
    {
        if (length > MaxPayload)
        {
            throw new FrameTooLargeException((uint)length);
        }
    }
}
