using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace OmniToolbox.NativeDragHotbarLocal;

internal static class WorkshopGameKeySchema
{
    // Describe the actual pointer-free byte layout and input identities, independent
    // of the dependency assembly's build ID. Unknown layouts fail closed.
    internal static string Describe(Type keybind, Type inputIds)
    {
        string Layout(Type type)
        {
            if (type.IsEnum)
                return type.Name + ":" + Layout(Enum.GetUnderlyingType(type)) + "[" + string.Join(",",
                    Enum.GetNames(type).Order(StringComparer.Ordinal).Select(name => name + "="
                        + Convert.ToInt64(Enum.Parse(type, name), CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture))) + "]";
            if (type.IsPrimitive && !type.IsPointer && type != typeof(IntPtr) && type != typeof(UIntPtr)) return type.Name;
            if (!type.IsValueType || type.IsPointer || type.IsByRefLike) throw new InvalidDataException("不支持的按键内存布局。");
            var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (type.GetCustomAttribute<InlineArrayAttribute>() is { } inline)
            {
                if (fields.Length != 1 || inline.Length < 1) throw new InvalidDataException("按键数组布局无效。");
                return "array(" + inline.Length.ToString(CultureInfo.InvariantCulture) + "," + Layout(fields[0].FieldType) + ")";
            }
            var format = type.StructLayoutAttribute;
            if (format?.Value != LayoutKind.Explicit) throw new InvalidDataException("尚未验证的按键布局。");
            return type.Name + ":" + format.Size.ToString(CultureInfo.InvariantCulture) + "["
                + string.Join(",", fields.OrderBy(f => f.Name, StringComparer.Ordinal).Select(field => field.Name + "@"
                    + (field.GetCustomAttribute<FieldOffsetAttribute>()?.Value
                        ?? throw new InvalidDataException("按键字段缺少位置。")).ToString(CultureInfo.InvariantCulture)
                    + "=" + Layout(field.FieldType))) + "]";
        }
        return Layout(keybind) + "|" + Layout(inputIds);
    }

    internal static string Fingerprint(Type keybind, Type inputIds)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Describe(keybind, inputIds))));
}
