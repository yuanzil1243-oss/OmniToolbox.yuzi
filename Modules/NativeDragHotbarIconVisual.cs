using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Lumina.Data.Files;
using OmniToolbox.Host;

namespace OmniToolbox.NativeDragHotbarLocal;

internal sealed class NativeHotbarIconVisual
{
    private readonly Dictionary<(string Bar, int Slot), NativeIconAnimation> animations = [];
    private readonly UldFile? sourceLayout;
    private readonly Func<string, (ImTextureID Handle, int Width, int Height)>? sourceTexture;
    private UldFile? layout;
    private bool attempted;
    public NativeHotbarIconVisual() { }
    // The isolated renderer can supply real ULD/atlas data without initializing game services.
    internal NativeHotbarIconVisual(UldFile sourceLayout,
        Func<string, (ImTextureID Handle, int Width, int Height)> sourceTexture)
    { this.sourceLayout = sourceLayout; this.sourceTexture = sourceTexture; }
    public string Status { get; private set; } = "等待原生图层资源";
    public void Clear() => animations.Clear();
    public void Trigger(string bar, int slot, ulong identity, double seconds)
    {
        if (!animations.TryGetValue((bar, slot), out var animation)) animations[(bar, slot)] = animation = new();
        animation.Trigger(identity, seconds);
    }
    public void DrawFeedback(ImDrawListPtr list, string bar, int slot, ulong identity,
        Vector2 min, Vector2 max, double seconds, float alpha)
    {
        if (!animations.TryGetValue((bar, slot), out var animation)) return;
        var pulse = animation.Pulse(identity, seconds);
        if (pulse <= 0) return;
        Sprite(list, 3, 14, min, max, HotbarPresentation.WithOpacity(0xFFFFFFFF, pulse * alpha));
        var spread = (max - min) * ((1 - pulse) * 0.10f);
        Sprite(list, 3, 16, min - spread, max + spread,
            HotbarPresentation.WithOpacity(0xFFFFFFFF, pulse * 0.55f * alpha));
    }
    public void DrawLock(ImDrawListPtr list, bool locked, Vector2 position, float scale, float alpha)
        => Sprite(list, 6, locked ? 2 : 1, position, position + new Vector2(20, 24) * scale,
            HotbarPresentation.WithOpacity(0xFFFFFFFF, alpha));
    public bool LockButton(ImDrawListPtr list, bool locked, Vector2 position, float scale, float alpha, bool input)
    {
        ImGui.SetCursorScreenPos(position);
        var clicked = ImGui.InvisibleButton("##SuperHotbarLock", new Vector2(20, 24) * scale);
        DrawLock(list, locked, position, scale, alpha);
        return input && clicked;
    }

    private bool Load()
    {
        if (attempted) return layout is not null;
        attempted = true;
        try
        {
            var uld = sourceLayout ?? DalamudServices.DataManager.GetFile<UldFile>("ui/uld/ActionBar.uld")
                ?? throw new InvalidDataException("ActionBar.uld 不可用");
            if (uld.Parts.Single(p => p.Id == 1).Parts.Length != 162 || uld.Parts.Single(p => p.Id == 4).Parts.Length != 81
                || uld.Parts.Single(p => p.Id == 2).Parts.Length != 10 || uld.Parts.Single(p => p.Id == 3).Parts.Length < 17)
                throw new InvalidDataException("原生图层布局与已核对版本不一致");
            layout = uld; Status = "原生 ActionBar 图层已就绪"; return true;
        }
        catch (Exception error)
        {
            Status = "原生图层不可用：" + error.Message;
            DalamudServices.PluginLog.Warning(error, "热键栏工坊：保留图标和文字，暂停原生图层"); return false;
        }
    }

    private bool Sprite(ImDrawListPtr list, int partList, int part, Vector2 min, Vector2 max, uint tint = 0xFFFFFFFF)
    {
        if (!Load()) return false;
        var parts = layout!.Parts.Single(p => p.Id == partList).Parts;
        if (part < 0 || part >= parts.Length) return false;
        var rect = parts[part];
        var asset = layout.AssetData.Single(a => a.Id == rect.TextureId);
        var path = new string(asset.Path).TrimEnd('\0');
        // All four paths are taken from the current client's ULD. HR atlas coordinates are 2x.
        var hrPath = path.Replace(".tex", "_hr1.tex");
        ImTextureID handle; int width, height;
        if (sourceTexture is not null) (handle, width, height) = sourceTexture(hrPath);
        else
        {
            var wrap = DalamudServices.TextureProvider.GetFromGame(hrPath).GetWrapOrDefault();
            if (wrap is null) return false;
            handle = wrap.Handle; width = wrap.Width; height = wrap.Height;
        }
        if (width <= 1 || height <= 1) return false;
        var uv = new Vector2(2f / width, 2f / height);
        list.AddImage(handle, min, max, new Vector2(rect.U, rect.V) * uv,
            new Vector2(rect.U + rect.W, rect.V + rect.H) * uv, tint);
        return true;
    }

    internal static void Text(ImDrawListPtr list, string text, Vector2 at, float size, uint color, float outline = 1)
    {
        var font = ImGui.GetFont();
        var alpha = color & 0xFF000000;
        at = new(MathF.Round(at.X), MathF.Round(at.Y)); size = Math.Max(6, MathF.Round(size));
        // One physical-pixel edge, with no scaled multi-pixel halo or fractional glyph positions.
        list.AddText(font, size, at + new Vector2(-1,0), alpha, text);
        list.AddText(font, size, at + new Vector2(1,0), alpha, text);
        list.AddText(font, size, at + new Vector2(0,-1), alpha, text);
        list.AddText(font, size, at + new Vector2(0,1), alpha, text);
        list.AddText(font, size, at, color, text);
    }

    private static void StyledText(ImDrawListPtr list, string text, Vector2 anchor, Vector2 align,
        NativeHotbarFontStyle style, float scale, float maxWidth, NativeHotbarGameFonts? fonts, uint color)
    {
        var pixels = style.Pixels(scale);
        var scope = fonts?.PushPixels(style, pixels);
        try
        {
            var measured = ImGui.CalcTextSize(text) * (pixels / ImGui.GetFontSize());
            if (measured.X > maxWidth)
            {
                pixels = Math.Max(6, (int)MathF.Floor(pixels * maxWidth / measured.X));
                scope?.Dispose(); scope = null;
                scope = fonts?.PushPixels(style, pixels);
                measured = ImGui.CalcTextSize(text) * (pixels / ImGui.GetFontSize());
            }
            Text(list, text, anchor - measured * align, pixels, color);
        }
        finally { scope?.Dispose(); }
    }

    public void Draw(ImDrawListPtr list, string bar, int slot, ulong identity, NativeSlotState state,
        string shortcut, Vector2 min, Vector2 max, double seconds, float alpha)
        => DrawStyled(list, bar, slot, identity, state, shortcut, min, max, seconds, alpha,
            null, NativeHotbarTextStyles.Default, true);

    internal void DrawStyled(ImDrawListPtr list, string bar, int slot, ulong identity, NativeSlotState state,
        string shortcut, Vector2 min, Vector2 max, double seconds, float alpha,
        NativeHotbarGameFonts? fonts, NativeHotbarTextStyles styles, bool frame)
    {
        var size = max - min; var scale = size / 48;
        var white = HotbarPresentation.WithOpacity(0xFFFFFFFF, alpha);
        if (!state.Usable) list.AddRectFilled(min + Vector2.One, max - Vector2.One,
            HotbarPresentation.WithOpacity(0x55000000, alpha), 3);
        if (frame) Sprite(list, 3, 0, min, max, white);
        if (NativeIconVisualRules.Cooling(state))
        {
            // ULD component 1001 node 15/17 uses (2,2),44x46 within a 48x48 icon.
            Sprite(list, NativeIconVisualRules.RecastPartList(state), NativeIconVisualRules.RecastFrame(state),
                min + new Vector2(2, 2) * scale, min + new Vector2(46, 48) * scale, white);
        }
        if (state.Highlighted) Sprite(list, 3, NativeIconVisualRules.AntsFrame(seconds), min, max, white);
        if (!animations.TryGetValue((bar, slot), out var animation)) animations[(bar, slot)] = animation = new();
        var flash = animation.Observe(identity, state, seconds);
        var pulse = animation.Pulse(identity, seconds);
        if (pulse > 0)
        {
            // ActionBar frame atlas: bright press edge followed by an expanding fading halo.
            Sprite(list, 3, 14, min, max, HotbarPresentation.WithOpacity(0xFFFFFFFF, pulse * alpha));
            var spread = size * ((1 - pulse) * 0.10f);
            Sprite(list, 3, 16, min - spread, max + spread,
                HotbarPresentation.WithOpacity(0xFFFFFFFF, pulse * 0.55f * alpha));
        }
        if (flash > 0)
        {
            var expand = size * ((1 - flash) * 0.125f);
            Sprite(list, 3, 16, min - expand, max + expand,
                HotbarPresentation.WithOpacity(0xFFFFFFFF, flash * 0.65f * alpha));
        }
        if (state.Seconds > 0)
        {
            var timer = state.Seconds.ToString();
            StyledText(list, timer, (min + max) / 2 + new Vector2(0, size.Y * 0.06f), new(0.5f),
                styles.Timer, scale.Y, size.X * 0.9f, fonts, white);
        }
        if (NativeIconVisualRules.ShowCharges(state))
        {
            // ULD node 10: 20x20 charge digit at (28,28), including red zero and one.
            if (!Sprite(list, 2, NativeIconVisualRules.ChargeDigit(state), min + new Vector2(28) * scale, max, white))
                Text(list, state.Charges.ToString(), min + new Vector2(31, 28) * scale, size.Y * 0.38f,
                    state.Charges == 0 ? 0xFF3333FF : white);
        }
        if (state.ItemCount is { } quantity)
        {
            var text = NativeHotbarItems.CountText(quantity);
            // Native icon node 9: x=4,y=34,width=40, right aligned; text is "x<count>".
            StyledText(list, text, min + new Vector2(44, 34) * scale, new(1, 0),
                styles.Quantity, scale.Y, size.X * (40f / 48f), fonts, white);
        }
        if (shortcut.Length > 0)
        {
            StyledText(list, shortcut, min + Vector2.One * scale, Vector2.Zero,
                styles.Shortcut, scale.Y, size.X * 0.95f, fonts, white);
        }
    }
}
