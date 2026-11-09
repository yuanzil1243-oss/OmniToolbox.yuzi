using System;
using System.Collections.Generic;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace OmniToolbox.NativeDragHotbarLocal;

internal sealed class NativeHotbarGameFonts : IDisposable
{
    private readonly Dictionary<(byte Family, int Pixels), IFontHandle> handles = [];
    private Func<GameFontStyle, IFontHandle>? create;
    private IFontAtlas? ownedAtlas;
    internal bool Started => create is not null;
    public string Status { get; private set; } = "等待游戏字体资源";
    public void Start(IFontAtlas atlas) { Dispose(); create = atlas.NewGameFontHandle; Status = "将跟随原生热键栏文字节点的游戏字体与字号"; }
    internal void StartOwned(IFontAtlas atlas) { Start(atlas); ownedAtlas = atlas; }
    internal static GameFontFamily Family(byte family) => family switch
    {
        1 => GameFontFamily.MiedingerMid, 2 => GameFontFamily.Meidinger, 3 => GameFontFamily.TrumpGothic,
        4 => GameFontFamily.Jupiter, 5 => GameFontFamily.JupiterNumeric, _ => GameFontFamily.Axis,
    };
    internal IDisposable? Push(NativeHotbarFontStyle style)
        => PushPixels(style, style.Pixels());
    // Full-size body text uses Axis, not the small numeric shortcut font.
    internal static NativeHotbarFontStyle TooltipStyle => new(0, 14);
    internal IDisposable? PushTooltip() => Push(TooltipStyle);
    internal IDisposable? PushPixels(NativeHotbarFontStyle style, int pixels)
    {
        if (create is null || !style.Valid) return null;
        try
        {
            var key = (style.Family, Math.Clamp(pixels, 6, 256));
            if (!handles.TryGetValue(key, out var handle))
            {
                if (handles.Count >= 128) return null;
                handles.Add(key, handle = create(new(Family(style.Family), key.Item2)));
            }
            Status = handle.LoadException is { } error ? "游戏字体暂不可用，保留备用文字：" + error.Message
                : handle.Available ? "正在使用原生热键栏节点对应的游戏字体与字号" : "游戏字体加载中，暂用备用文字";
            // The managed handle owns the font lock, push/pop and async-atlas rebuild lifetime.
            return handle.Push();
        }
        catch (Exception error) { Status = "游戏字体暂不可用，保留备用文字：" + error.Message; return null; }
    }
    public void Dispose()
    {
        create = null;
        foreach (var handle in handles.Values) handle.Dispose();
        handles.Clear(); Status = "等待游戏字体资源";
        ownedAtlas?.Dispose(); ownedAtlas = null;
    }
}

internal static unsafe class NativeHotbarFontReader
{
    private static NativeHotbarFontStyle Read(AtkTextNode* node, NativeHotbarFontStyle fallback)
    {
        if (node == null) return fallback;
        var result = new NativeHotbarFontStyle((byte)node->FontType, node->FontSize);
        return result.Valid ? result : fallback;
    }
    internal static NativeHotbarTextStyles Read(int bar, int slot)
    {
        var fallback = NativeHotbarTextStyles.Default;
        var addon = (AddonActionBarBase*)SuperNativeArchive.Addon(bar);
        if (addon == null || slot is < 0 or >= 12 || addon->ActionBarSlotVector.LongCount != 12) return fallback;
        ref var source = ref addon->ActionBarSlotVector[slot];
        var icon = source.ComponentDragDrop == null ? null : source.ComponentDragDrop->AtkComponentIcon;
        return new(Read(source.ControlHintTextNode, fallback.Shortcut),
            Read(icon == null ? null : icon->GetTextNodeById(8), fallback.Timer),
            Read(icon == null ? null : icon->QuantityText, fallback.Quantity));
    }
}
