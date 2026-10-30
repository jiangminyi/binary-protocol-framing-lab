using System;

namespace Framing;

/// <summary>Castagnoli CRC，用于传输错误检测，不提供消息认证。</summary>
public static class Crc32C
{
    private static readonly uint[] Table = CreateTable();
    internal const uint Initial = uint.MaxValue;

    public static uint Compute(ReadOnlySpan<byte> data) => ~Update(Initial, data);

    internal static uint Update(uint state, ReadOnlySpan<byte> data)
    {
        foreach (byte value in data) state = Table[(state ^ value) & 255] ^ (state >> 8);
        return state;
    }

    private static uint[] CreateTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            uint value = i;
            for (int bit = 0; bit < 8; bit++)
                value = (value >> 1) ^ ((value & 1) == 0 ? 0u : 0x82F63B78u);
            table[i] = value;
        }
        return table;
    }
}
