using System;
using System.Linq;
using System.Text;
using Framing;

var wire = new[] { new Frame(10, Encoding.UTF8.GetBytes("你好，分片网络")), new Frame(11, Array.Empty<byte>()) }
    .SelectMany(WireFormat.Encode).ToArray();
var decoder = new FrameDecoder();
for (int offset = 0; offset < wire.Length;)
{
    int count = Math.Min(3, wire.Length - offset);
    var batch = decoder.Feed(wire.AsSpan(offset, count));
    foreach (var frame in batch.Frames)
        Console.WriteLine($"序号={frame.Sequence} 长度={frame.PayloadLength} 内容={Encoding.UTF8.GetString(frame.PayloadToArray())}");
    if (batch.Error.HasValue) throw new InvalidOperationException(batch.Error.ToString());
    offset += batch.Consumed;
}
if (decoder.Complete().HasValue) throw new InvalidOperationException("输入被截断");
Console.WriteLine("所有分片已合并，流在完整帧边界结束。");
