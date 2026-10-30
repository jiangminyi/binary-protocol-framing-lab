using System;
using System.Buffers.Binary;

namespace Framing;

/// <summary>载荷归帧独占，调用方只能取得副本。</summary>
public sealed class Frame
{
    private readonly byte[] payload;
    public uint Sequence { get; }
    public int PayloadLength => payload.Length;
    public Frame(uint sequence, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > WireFormat.AbsoluteMaxPayload) throw new ArgumentOutOfRangeException(nameof(payload));
        Sequence = sequence;
        this.payload = payload.ToArray();
    }
    internal Frame(uint sequence, byte[] ownedPayload, bool takeOwnership)
    {
        Sequence = sequence;
        payload = takeOwnership ? ownedPayload : (byte[])ownedPayload.Clone();
    }
    public byte[] PayloadToArray() => (byte[])payload.Clone();
    internal ReadOnlySpan<byte> Bytes => payload;
}

public static class WireFormat
{
    public const int HeaderLength = 16;
    public const int AbsoluteMaxPayload = 1024 * 1024;
    public const int DefaultMaxPayload = 64 * 1024;

    public static byte[] Encode(Frame frame)
    {
        if (frame is null) throw new ArgumentNullException(nameof(frame));
        var bytes = new byte[HeaderLength + frame.PayloadLength];
        bytes[0] = 0x43; bytes[1] = 0x58; bytes[2] = 1; bytes[3] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)frame.PayloadLength);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), frame.Sequence);
        frame.Bytes.CopyTo(bytes.AsSpan(HeaderLength));
        uint crc = Crc32C.Update(Crc32C.Initial, bytes.AsSpan(0, 12));
        crc = Crc32C.Update(crc, frame.Bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), ~crc);
        return bytes;
    }
}
