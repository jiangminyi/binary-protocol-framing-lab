using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Framing;

var specs = new (string Name, Action Run)[]
{
    ("CRC32C 标准检查向量", () => Equal(0xE3069283u, Crc32C.Compute(Encoding.ASCII.GetBytes("123456789")))),
    ("CRC 空输入", () => Equal(0u, Crc32C.Compute(Array.Empty<byte>()))),
    ("编码器与独立位级参考编码一致", () =>
    {
        var random = new Random(711);
        for (int length = 0; length < 400; length++)
        {
            var payload = new byte[length]; random.NextBytes(payload);
            Bytes(ReferenceEncode(uint.MaxValue - (uint)length, payload), WireFormat.Encode(new Frame(uint.MaxValue - (uint)length, payload)));
        }
    }),
    ("所有双分片边界均可重组", () =>
    {
        byte[] payload = Encoding.UTF8.GetBytes("不假设 TCP 读取就是消息边界");
        byte[] wire = ReferenceEncode(72, payload);
        for (int split = 0; split <= wire.Length; split++)
        {
            var decoder = new FrameDecoder();
            var first = decoder.Feed(wire.AsSpan(0, split));
            var second = decoder.Feed(wire.AsSpan(split));
            Check(!first.Error.HasValue && !second.Error.HasValue);
            var frames = first.Frames.Concat(second.Frames).ToArray();
            Equal(1, frames.Length); Equal(72u, frames[0].Sequence); Bytes(payload, frames[0].PayloadToArray());
            Check(!decoder.Complete().HasValue);
        }
    }),
    ("逐字节分片不会提前输出", () =>
    {
        var wire = ReferenceEncode(3, new byte[] { 4, 5, 6 });
        var decoder = new FrameDecoder();
        for (int i = 0; i < wire.Length; i++)
        {
            var batch = decoder.Feed(wire.AsSpan(i, 1));
            Equal(i == wire.Length - 1 ? 1 : 0, batch.Frames.Count); Equal(1, batch.Consumed); Check(!batch.Error.HasValue);
        }
    }),
    ("零长度帧在头部完成时立即输出", () =>
    {
        var decoder = new FrameDecoder(); var batch = decoder.Feed(ReferenceEncode(uint.MaxValue, Array.Empty<byte>()));
        Equal(1, batch.Frames.Count); Equal(0, batch.Frames[0].PayloadLength); Equal(uint.MaxValue, batch.Frames[0].Sequence); Equal(0, decoder.BufferedBytes);
    }),
    ("一次输入包含多个帧", () =>
    {
        byte[] wire = Enumerable.Range(0, 50).SelectMany(i => ReferenceEncode((uint)i, new byte[i])).ToArray();
        var batch = new FrameDecoder().Feed(wire); Equal(50, batch.Frames.Count); Equal(wire.Length, batch.Consumed); Check(!batch.Error.HasValue);
    }),
    ("300 组随机流与独立模型一致", RandomStreams),
    ("所有非空截断位置均在 EOF 报错", () =>
    {
        byte[] wire = ReferenceEncode(5, new byte[33]);
        for (int cut = 1; cut < wire.Length; cut++)
        {
            var decoder = new FrameDecoder(); var batch = decoder.Feed(wire.AsSpan(0, cut));
            Equal(0, batch.Frames.Count); Check(!batch.Error.HasValue);
            Equal(DecodeError.TruncatedFrame, decoder.Complete()); Equal(0, decoder.BufferedBytes);
        }
    }),
    ("空流与重复 Complete 是合法结束", () =>
    {
        var decoder = new FrameDecoder(); Check(!decoder.Complete().HasValue); Check(!decoder.Complete().HasValue);
        Equal(DecodeError.DecoderClosed, decoder.Feed(Array.Empty<byte>()).Error);
    }),
    ("完成后不接收新数据", () =>
    {
        var decoder = new FrameDecoder(); decoder.Feed(ReferenceEncode(0, Array.Empty<byte>())); decoder.Complete();
        var batch = decoder.Feed(ReferenceEncode(1, new byte[1])); Equal(0, batch.Consumed); Equal(DecodeError.DecoderClosed, batch.Error);
    }),
    ("拒绝错误魔数", () => HeaderFault(0, 0, DecodeError.BadMagic)),
    ("拒绝未知版本", () => HeaderFault(2, 2, DecodeError.UnsupportedVersion)),
    ("拒绝未定义 flags", () => HeaderFault(3, 1, DecodeError.UnsupportedFlags)),
    ("无符号巨大长度在头部阶段拒绝", () =>
    {
        byte[] wire = ReferenceEncode(1, Array.Empty<byte>()); BinaryPrimitives.WriteUInt32LittleEndian(wire.AsSpan(4), uint.MaxValue);
        var decoder = new FrameDecoder(8); var batch = decoder.Feed(wire);
        Equal(DecodeError.PayloadTooLarge, batch.Error); Equal(16, batch.Consumed); Equal(0, decoder.BufferedBytes);
    }),
    ("最大长度边界以及零容量解析器", () =>
    {
        var decoder = new FrameDecoder(32); Equal(1, decoder.Feed(ReferenceEncode(0, new byte[32])).Frames.Count);
        Equal(DecodeError.PayloadTooLarge, decoder.Feed(ReferenceEncode(1, new byte[33])).Error);
        var zero = new FrameDecoder(0); Equal(1, zero.Feed(ReferenceEncode(0, Array.Empty<byte>())).Frames.Count);
        Equal(DecodeError.PayloadTooLarge, zero.Feed(ReferenceEncode(0, new byte[1])).Error);
    }),
    ("构造参数越界立即拒绝", () =>
    {
        Throws<ArgumentOutOfRangeException>(() => new FrameDecoder(-1));
        Throws<ArgumentOutOfRangeException>(() => new FrameDecoder(WireFormat.AbsoluteMaxPayload + 1));
        Throws<ArgumentOutOfRangeException>(() => new Frame(0, new byte[WireFormat.AbsoluteMaxPayload + 1]));
    }),
    ("帧中每个单比特破坏都不能静默通过", () =>
    {
        byte[] wire = ReferenceEncode(123, new byte[] { 4, 8, 15, 16, 23, 42 });
        for (int i = 0; i < wire.Length; i++) for (int bit = 0; bit < 8; bit++)
        {
            byte[] corrupt = (byte[])wire.Clone(); corrupt[i] ^= (byte)(1 << bit);
            var decoder = new FrameDecoder(100); var batch = decoder.Feed(corrupt);
            Equal(0, batch.Frames.Count); Check(batch.Error.HasValue || decoder.Complete().HasValue);
        }
    }),
    ("错误前已完成帧保留且错误后字节未消费", () =>
    {
        byte[] good = ReferenceEncode(1, new byte[] { 9 });
        byte[] bad = ReferenceEncode(2, new byte[] { 8 }); bad[16] ^= 1;
        byte[] input = good.Concat(bad).Concat(good).ToArray();
        var decoder = new FrameDecoder(); var batch = decoder.Feed(input);
        Equal(1, batch.Frames.Count); Equal(1u, batch.Frames[0].Sequence);
        Equal(good.Length + bad.Length, batch.Consumed); Equal(DecodeError.ChecksumMismatch, batch.Error);
        var after = decoder.Feed(good); Equal(0, after.Consumed); Equal(0, after.Frames.Count);
        Equal(DecodeError.ChecksumMismatch, after.Error); Equal(DecodeError.ChecksumMismatch, decoder.Complete());
    }),
    ("调用方输入修改不改变已缓存内容", () =>
    {
        byte[] wire = ReferenceEncode(9, new byte[] { 1, 2, 3 }); var decoder = new FrameDecoder();
        decoder.Feed(wire.AsSpan(0, 17)); wire[16] = 99;
        var batch = decoder.Feed(wire.AsSpan(17)); Bytes(new byte[] { 1, 2, 3 }, batch.Frames.Single().PayloadToArray());
    }),
    ("帧构造与载荷读取均隔离修改", () =>
    {
        var original = new byte[] { 1, 2 }; var frame = new Frame(1, original); original[0] = 7;
        var output = frame.PayloadToArray(); output[1] = 9; Bytes(new byte[] { 1, 2 }, frame.PayloadToArray());
    }),
    ("空 Feed 不丢失部分头部", () =>
    {
        var decoder = new FrameDecoder(); byte[] wire = ReferenceEncode(1, Array.Empty<byte>());
        decoder.Feed(wire.AsSpan(0, 7)); var batch = decoder.Feed(Array.Empty<byte>());
        Equal(0, batch.Consumed); Equal(7, decoder.BufferedBytes); Equal(1, decoder.Feed(wire.AsSpan(7)).Frames.Count);
    })
};
int failed = 0;
foreach (var spec in specs)
{
    try { spec.Run(); Console.WriteLine($"通过：{spec.Name}"); }
    catch (Exception error) { failed++; Console.Error.WriteLine($"失败：{spec.Name}: {error}"); }
}
Console.WriteLine($"{specs.Length - failed}/{specs.Length} 规格通过");
return failed == 0 ? 0 : 1;

static void RandomStreams()
{
    var random = new Random(20261030);
    for (int iteration = 0; iteration < 300; iteration++)
    {
        var expected = new List<(uint Sequence, byte[] Payload)>();
        for (int i = 0; i < random.Next(1, 30); i++)
        {
            byte[] payload = new byte[random.Next(0, 200)]; random.NextBytes(payload);
            expected.Add(((uint)random.NextInt64(0, (long)uint.MaxValue + 1), payload));
        }
        byte[] wire = expected.SelectMany(item => ReferenceEncode(item.Sequence, item.Payload)).ToArray();
        var decoder = new FrameDecoder(); var actual = new List<Frame>();
        for (int offset = 0; offset < wire.Length;)
        {
            int count = Math.Min(random.Next(1, 71), wire.Length - offset);
            var batch = decoder.Feed(wire.AsSpan(offset, count));
            Check(!batch.Error.HasValue); Equal(count, batch.Consumed); actual.AddRange(batch.Frames); offset += count;
        }
        Check(!decoder.Complete().HasValue); Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        { Equal(expected[i].Sequence, actual[i].Sequence); Bytes(expected[i].Payload, actual[i].PayloadToArray()); }
    }
}

static void HeaderFault(int index, byte value, DecodeError error)
{
    byte[] wire = ReferenceEncode(0, new byte[3]); wire[index] = value;
    var decoder = new FrameDecoder(); var result = decoder.Feed(wire);
    Equal(error, result.Error); Equal(16, result.Consumed); Check(decoder.IsFaulted);
}

// 独立参考：手工小端写入，逐位 CRC；不调用被测编码器或 CRC 实现。
static byte[] ReferenceEncode(uint sequence, byte[] payload)
{
    var bytes = new List<byte> { 0x43, 0x58, 1, 0 };
    foreach (uint value in new[] { (uint)payload.Length, sequence })
        for (int i = 0; i < 4; i++) bytes.Add((byte)(value >> (8 * i)));
    uint crc = uint.MaxValue;
    foreach (byte item in bytes.Concat(payload))
    {
        crc ^= item;
        for (int i = 0; i < 8; i++) crc = (crc & 1) == 1 ? (crc >> 1) ^ 0x82F63B78 : crc >> 1;
    }
    crc = ~crc;
    for (int i = 0; i < 4; i++) bytes.Add((byte)(crc >> (8 * i)));
    bytes.AddRange(payload); return bytes.ToArray();
}

static void Check(bool condition) { if (!condition) throw new Exception("断言失败"); }
static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"预期 {expected}，实际 {actual}"); }
static void Bytes(byte[] expected, byte[] actual) => Check(expected.SequenceEqual(actual));
static void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new Exception($"应抛出 {typeof(T).Name}");
}
