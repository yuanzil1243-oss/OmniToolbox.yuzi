using System;
using System.Buffers.Binary;
using System.IO;

namespace OmniToolbox.NativeDragHotbarLocal;

internal static class NativeHotbarExecution
{
    // The shipped ClientStructs ExecuteSlot signature points at an indexed wrapper in the current CN build.
    // Resolve its three agreeing tail branches instead of calling that wrapper with a HotbarSlot pointer.
    // If its instruction shape changes, fail closed and require a rebuilt module.
    private static readonly byte[] Expected = Convert.FromHexString(
        "4C8BC94183F810734583FA127519418BC04869D0E80000004881C2A00501004803D1E9A95B0000" +
        "73258BCA498D91A000000048C1E104418BC04803C84869C1E8000000498BC94803D0E9825B0000" +
        "488D91A0220100498BC9E9735B0000");

    internal static long Resolve(ReadOnlySpan<byte> code, long address)
    {
        if (code.Length < Expected.Length) throw new InvalidDataException("热键执行入口过短。");
        for (var i = 0; i < Expected.Length; i++)
        {
            if (i is >= 35 and <= 38 or >= 74 and <= 77 or >= 89 and <= 92) continue;
            if (code[i] != Expected[i]) throw new InvalidDataException("当前客户端热键执行入口与本版本不兼容。");
        }
        var result = Target(code, address, 34);
        if (result != Target(code, address, 73) || result != Target(code, address, 88)) throw new InvalidDataException("热键执行入口分支校验不一致。");
        return result;
    }

    internal static void ValidateSlotExecutor(ReadOnlySpan<byte> code)
    {
        // CN 2026.10.06: prologue, followed by CommandType read from HotbarSlot* in RDX.
        ReadOnlySpan<byte> expected = [0x48,0x89,0x5C,0x24,0x08,0x48,0x89,0x6C,0x24,0x20,
            0x56,0x57,0x41,0x56,0x48,0x83,0xEC,0x40,0x0F,0xB6,0x82,0xC7,0,0,0];
        if (code.Length < expected.Length || !code[..expected.Length].SequenceEqual(expected))
            throw new InvalidDataException("原生热键格执行函数布局不匹配。");
    }

    internal static long SnapshotAddress(long address, long moduleBase, long searchBase,
        long textOffset, int textSize, int length)
    {
        if (moduleBase <= 0 || searchBase <= 0 || address < moduleBase || textOffset < 0
            || textSize <= 0 || length <= 0 || length > textSize)
            throw new InvalidDataException("原生代码校验范围无效。");
        var offset = address - moduleBase;
        if (offset < textOffset || offset - textOffset > textSize - length
            || offset > long.MaxValue - (length - 1L)
            || searchBase > long.MaxValue - offset - (length - 1L))
            throw new InvalidDataException("原生代码校验目标超出游戏代码段。");
        return searchBase + offset;
    }

    private static long Target(ReadOnlySpan<byte> code, long address, int offset) =>
        checked(address + offset + 5 + BinaryPrimitives.ReadInt32LittleEndian(code.Slice(offset + 1, 4)));
}
