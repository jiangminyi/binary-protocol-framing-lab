using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Framing;

public enum DecodeError
{
    BadMagic, UnsupportedVersion, UnsupportedFlags, PayloadTooLarge,
    ChecksumMismatch, TruncatedFrame, DecoderClosed
}

public sealed class DecodeBatch
{
    public IReadOnlyList<Frame> Frames { get; }
    public int Consumed { get; }
    public DecodeError? Error { get; }
    internal DecodeBatch(List<Frame> frames, int consumed, DecodeError? error)
    {
        Frames = frames.AsReadOnly(); Consumed = consumed; Error = error;
    }
}

/// <summary>单使用者增量解析器。协议错误后永久终止，不扫描后续魔数尝试恢复。</summary>
public sealed class FrameDecoder
{
    private readonly byte[] header = new byte[WireFormat.HeaderLength];
    private readonly int maximum;
    private int headerCount;
    private byte[]? payload;
    private int payloadCount;
    private uint sequence;
    private uint expectedChecksum;
    private uint checksum;
    private DecodeError? fault;
    private bool completed;

    public FrameDecoder(int maximumPayloadLength = WireFormat.DefaultMaxPayload)
    {
        if (maximumPayloadLength < 0 || maximumPayloadLength > WireFormat.AbsoluteMaxPayload)
            throw new ArgumentOutOfRangeException(nameof(maximumPayloadLength));
        maximum = maximumPayloadLength;
    }
    public int BufferedBytes => headerCount + payloadCount;
    public bool IsFaulted => fault.HasValue;

    public DecodeBatch Feed(ReadOnlySpan<byte> input)
    {
        var frames = new List<Frame>();
        if (fault.HasValue || completed)
            return new DecodeBatch(frames, 0, fault ?? DecodeError.DecoderClosed);
        int consumed = 0;
        while (consumed < input.Length)
        {
            if (headerCount < WireFormat.HeaderLength)
            {
                int count = Math.Min(WireFormat.HeaderLength - headerCount, input.Length - consumed);
                input.Slice(consumed, count).CopyTo(header.AsSpan(headerCount));
                consumed += count; headerCount += count;
                if (headerCount < WireFormat.HeaderLength) break;
                DecodeError? error = StartPayload();
                if (error.HasValue) { Fail(error.Value); break; }
            }
            // 完整头部也可能代表零长度帧，不依赖下一次 Feed 才交付。
            int remaining = payload!.Length - payloadCount;
            int take = Math.Min(remaining, input.Length - consumed);
            ReadOnlySpan<byte> part = input.Slice(consumed, take);
            part.CopyTo(payload.AsSpan(payloadCount));
            checksum = Crc32C.Update(checksum, part);
            consumed += take; payloadCount += take;
            if (payloadCount != payload.Length) break;
            if (~checksum != expectedChecksum) { Fail(DecodeError.ChecksumMismatch); break; }
            frames.Add(new Frame(sequence, payload, takeOwnership: true));
            payload = null; payloadCount = 0; headerCount = 0;
        }
        return new DecodeBatch(frames, consumed, fault);
    }

    /// <summary>输入流结束。完整边界返回 null，残缺帧转为终止错误；重复调用结果稳定。</summary>
    public DecodeError? Complete()
    {
        if (fault.HasValue) return fault;
        if (!completed && headerCount != 0) Fail(DecodeError.TruncatedFrame);
        completed = true;
        return fault;
    }

    private DecodeError? StartPayload()
    {
        if (header[0] != 0x43 || header[1] != 0x58) return DecodeError.BadMagic;
        if (header[2] != 1) return DecodeError.UnsupportedVersion;
        if (header[3] != 0) return DecodeError.UnsupportedFlags;
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        // 先检查无符号长度，之后才转换和分配；0xFFFFFFFF 不会绕过边界。
        if (length > (uint)maximum) return DecodeError.PayloadTooLarge;
        sequence = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
        expectedChecksum = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
        checksum = Crc32C.Update(Crc32C.Initial, header.AsSpan(0, 12));
        payload = new byte[(int)length];
        payloadCount = 0;
        return null;
    }

    private void Fail(DecodeError error)
    {
        fault = error;
        payload = null; payloadCount = 0; headerCount = 0;
    }
}
