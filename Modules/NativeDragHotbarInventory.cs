using System;
using System.Buffers.Binary;
using System.IO;

namespace OmniToolbox.NativeDragHotbarLocal;

internal static class NativeHotbarInventory
{
    // The game's own action-bar receiver converts UI inventory positions through this call.
    // This covers sorted bag slots as well as the other inventory windows supported by the client.
    public const string Signature = "45 8B 4C 24 04 48 8D 54 24 30 45 8B 04 24 48 8D 4C 24 34 E8 ?? ?? ?? ?? 8B 74 24 34 0F BF 44 24 30 C1 E6 10 0B F0";
    public static long Resolve(ReadOnlySpan<byte> callsite, long address)
    {
        ReadOnlySpan<byte> before = [0x45,0x8B,0x4C,0x24,0x04,0x48,0x8D,0x54,0x24,0x30,0x45,0x8B,0x04,0x24,0x48,0x8D,0x4C,0x24,0x34,0xE8];
        ReadOnlySpan<byte> after = [0x8B,0x74,0x24,0x34,0x0F,0xBF,0x44,0x24,0x30,0xC1,0xE6,0x10,0x0B,0xF0];
        if (callsite.Length < 38 || !callsite[..20].SequenceEqual(before) || !callsite.Slice(24,14).SequenceEqual(after))
            throw new InvalidDataException("背包拖放位置转换入口不匹配。");
        return checked(address + 24 + BinaryPrimitives.ReadInt32LittleEndian(callsite.Slice(20,4)));
    }

    public static bool TryNormalizeResult(uint itemId, short type, out byte normalizedType, out uint normalizedId)
    {
        normalizedType = 0;
        normalizedId = 0;
        if (itemId == 0 || itemId == 9999 || type < 0) return false;
        normalizedType = (byte)type;
        normalizedId = itemId;
        return HotbarRules.IsSupportedType(normalizedType) && HotbarRules.IsValidId(normalizedType, normalizedId);
    }

    public static bool IsTransient(byte type) => type is 3 or 5 or 14;

    public static bool IsNormalized(byte requestedType, uint requestedId, byte actualType, uint actualId)
        => HotbarRules.IsSupportedType(actualType) && HotbarRules.IsValidId(actualType, actualId)
           && (requestedType switch
           {
               3 or 14 => actualType == 2,
               5 => actualType == 4,
               _ => actualType == requestedType && actualId == requestedId,
           });
}
