using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Config;
using Dalamud.Game.Command;
using OmenTools;
using OmenTools.OmenService;
using OmniToolbox.Common.Module.Abstractions;
using OmniToolbox.Common.Module.Enums;
using OmniToolbox.Common.Module.Models;
using OmniToolbox.Host;
using OmniToolbox.UI;
using OmniToolbox.UI.Theme;
using TreeTools.Lifecycle;
using Lumina.Excel.Sheets;

namespace OmniToolbox.NativeDragHotbarLocal;

[ModuleApiVersion(1)]
public sealed class NativeDragHotbar : OmniModuleBase
{
    public const string Version = "1.1.4";
    public override ModuleInfo Info { get; } = new() { Category = ModuleCategory.Interface,
        Author = "Yuzi", Commands = [new("ToggleCommand", "/draghotbar toggle")] };
    public override ModuleLocalization Localization { get; } = ModuleLocalization.Create(new Dictionary<string, string>
    {
        ["Title"] = "拖放热键栏",
        ["Description"] = "支持接收游戏原生技能与功能按钮，右键编辑图标、说明及自定义指令，支持自由布局和导入导出。",
        ["ToggleCommand"] = "显示或隐藏置顶热键栏；可追加 #序号或栏名称指定单栏",
        ["Help"] = "左键点击执行；从游戏技能页或原生热键栏拖入；右键编辑；未锁定时栏内左键拖动换位。按住 Alt + 左键可拖动整栏。已有内容的格子会询问是否替换。",
        ["Help2"] = "支持技能、通用技能、主指令、情感动作、坐骑、宠物、套装、用户宏及背包物品等原生热键类型。配置在本机共用，切换账号或角色沿用同一套设置。没有原生冷却动画或键盘绑定。",
        ["ShowHelp"] = "显示操作说明",
        ["Waiting"] = "请登录角色后配置热键栏。本地所有账号和角色共用一套配置。",
        ["Readonly"] = "共享配置、备份或旧配置迁移读取失败，已暂停编辑和写入。详情见日志。",
        ["Migrated"] = "已建立本地共享配置，迁移来源：{0}；旧文件已保留。",
        ["SaveError"] = "保存失败，修改仍在内存中。请检查日志与配置目录权限。",
        ["Retry"] = "重试保存",
        ["Recovered"] = "已从备份恢复；损坏的原文件已另存保留。",
        ["Show"] = "显示全部热键栏",
        ["Add"] = "新增热键栏",
        ["Remove"] = "删除当前栏…",
        ["RemoveTitle"] = "删除热键栏？###NDHRemove",
        ["RemoveConfirm"] = "此栏的全部 {0} 个已保存格子（包括隐藏格）将被删除。",
        ["ConfirmRemove"] = "确认删除",
        ["Name"] = "名称",
        ["Visible"] = "显示此栏",
        ["NativeLock"] = "原生热键栏锁定：{0}（仍可拖入）",
        ["Locked"] = "已锁定",
        ["Unlocked"] = "未锁定",
        ["HideEmpty"] = "隐藏空格（拖入时自动展开）",
        ["Layout"] = "布局",
        ["Horizontal"] = "横向",
        ["Vertical"] = "纵向",
        ["ApplyLayout"] = "应用布局",
        ["LayoutCount"] = "{0} × {1}，共 {2} 格",
        ["LayoutInvalid"] = "横向和纵向必须为正整数，每栏最多 120 格。",
        ["HiddenSlots"] = "另有 {0} 个格子暂时隐藏，其中 {1} 个有内容；扩大布局可恢复。",
        ["ExportCurrent"] = "导出当前栏",
        ["ExportAll"] = "导出全部设置",
        ["ImportClipboard"] = "从剪贴板导入…",
        ["Exported"] = "已复制导出文本至剪贴板，可粘贴保存为文本文件。",
        ["TransferError"] = "导入或导出失败：{0}",
        ["ImportPreview"] = "导入预览：{0} 条热键栏，{1} 个显示格子，{2} 个暂存隐藏格。",
        ["ImportGlobal"] = "同时恢复全局显示和操作说明设置",
        ["ImportNotice"] = "确认后追加为新栏，不覆盖原有栏。热键内容和自定义指令均包含在导入中，导入时不会执行指令。",
        ["ImportLimit"] = "当前有 {0} 条栏，加上导入后超过 12 条上限。请先取消并删除不需要的栏。",
        ["ConfirmImport"] = "确认追加导入",
        ["Imported"] = "已追加导入 {0} 条热键栏。",
        ["Scale"] = "缩放",
        ["Opacity"] = "不透明度",
        ["Recenter"] = "将此栏移到屏幕中央",
        ["Slot"] = "格子 {0}",
        ["Empty"] = "空格：拖入技能或右键编辑",
        ["Native"] = "原生动作",
        ["Command"] = "自定义指令",
        ["Editor"] = "编辑热键格###NDHEditor",
        ["EditorDrop"] = "拖到这里替换原生动作（保存后生效）",
        ["MacroDropHelp"] = "拖入此指令文本框：技能、功能按钮插入名称；情感动作插入其原生指令。",
        ["MacroDropAdded"] = "已插入指令文本：{0}；点击保存后生效。",
        ["MacroDropUnsupported"] = "无法取得此动作的原生命令，原文本未更改。",
        ["MacroDropFull"] = "追加后超出指令文本上限，原文本未更改。",
        ["DraftCopied"] = "已放入编辑草稿：{0}；点击保存后生效。",
        ["Mode"] = "执行模式",
        ["Source"] = "原生来源：{0} · 类型 {1} · ID {2}",
        ["Icon"] = "图标 ID",
        ["PickIcon"] = "选择图标…",
        ["RestoreIcon"] = "恢复原生图标",
        ["IconPriority"] = "图标优先级：/micon 指令 ＞ 自主选择 ＞ 原生或自动图标。纯指令且无图标时自动分配默认图标。",
        ["Tooltip"] = "悬浮说明",
        ["CommandHelp"] = "保留的原生动作会先执行；下面每行一条附加游戏或插件指令，按顺序立即发送，不实现宏等待。",
        ["NativePreserved"] = "保留并先执行原生动作：{0}（类型 {1} · ID {2}）",
        ["Save"] = "保存",
        ["Cancel"] = "取消",
        ["Clear"] = "清空此格",
        ["ReplaceTitle"] = "替换热键格？###NDHReplace",
        ["ReplaceText"] = "将“{0}”替换为“{1}”？",
        ["Replace"] = "确认替换",
        ["Unsupported"] = "此拖动来源暂不支持。可先放入游戏原生热键栏，再从原生栏拖入。",
        ["NativeError"] = "原生拖入或执行失败，详情见日志。",
        ["Copied"] = "已放入：{0}",
        ["CommandUsage"] = "用法：/draghotbar toggle [#序号|栏名称]；show 同样切换，on 显示，hide/off 隐藏。",
        ["CommandUnavailable"] = "请登录角色并确认配置可读后再切换热键栏。",
        ["VisibilityChanged"] = "{0}：{1}",
        ["Shown"] = "显示",
        ["Hidden"] = "隐藏",
        ["InvalidBar"] = "找不到唯一匹配的热键栏：{0}",
        ["Diagnostics"] = "最近的原生拖动：{0}",
    });

    private sealed class Settings { public bool ShowHelp = true; }
    private Settings config = new();
    private HotbarProfile profile = new();
    private ulong owner;
    private bool readOnly, recovering, dirty, profileLoaded;
    private string saveError = "", notice = "";
    private long noticeUntil;
    private int selected;
    private NativeHotbarBridge? bridge;
    private readonly NativeHotbarMacroIcons macroIcons = new();
    private bool iconWarningLogged;
    private readonly List<DropArea> targets = [];
    private Vector2 cursorOffset;
    private long targetsAt;
    private readonly Queue<DropRequest> received = new();
    private DropRequest? replacing;
    private DragHotbar? editBar;
    private HotbarEntry? editOriginal, draft;
    private int editIndex;
    private bool editorOpen;
    private bool editorVisible;
    private Vector2 editorMin, editorMax;
    private DragHotbar? localDragBar;
    private DragHotbar? movingBar;
    private int localDragIndex = -1;
    private long lastNativeDrop;
    private readonly HashSet<string> reposition = [];
    private bool nativeHotbarLocked;
    private bool lockReadWarningLogged;
    private bool refreshCommandBuffer;
    private string layoutBarId = "";
    private int layoutColumns, layoutRows;
    private HotbarShareDocument? pendingImport;
    private bool importGlobalSettings;
    private ulong importOwner;

    private sealed record DropArea(DragHotbar Bar, int Index, Vector2 Min, Vector2 Max, HotbarEntry Before,
        bool Editing = false, bool MacroInsertion = false);
    private sealed record DropRequest(ulong Owner, DragHotbar Bar, int Index, HotbarEntry Before, HotbarEntry Entry,
        bool Editing = false, bool MacroInsertion = false);

    public override bool HasSettings => true;
    private string L(string key, params object[] args)
    { var value = OmniLoc.Get(this, key); return args.Length == 0 ? value : string.Format(value, args); }
    private string ProfilePath => HotbarSharedProfiles.PathFor(DalamudServices.PluginInterface.GetPluginConfigDirectory());
    private string BarLabel(DragHotbar bar) => $"#{profile.Bars.IndexOf(bar) + 1} {bar.Name}";

    private bool ReadNativeHotbarLock()
    {
        try { lockReadWarningLogged = false; return DService.Instance().GameConfig.UiConfig.GetUInt("HotbarLock") != 0; }
        catch (Exception error)
        { if (!lockReadWarningLogged) DalamudServices.PluginLog.Warning(error, "拖放热键栏：读取原生热键栏锁定状态失败"); lockReadWarningLogged = true; return false; }
    }

    private static bool BeginStyledCombo(string label, string preview)
    {
        ImGui.PushStyleColor(ImGuiCol.FrameBg, 0xD04B4742);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, 0xE05B5650);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, 0xE044403B);
        ImGui.PushStyleColor(ImGuiCol.PopupBg, 0xE0504B45);
        ImGui.PushStyleColor(ImGuiCol.Border, 0xFFC5BEB4);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize, 1);
        var open = ImGui.BeginCombo(label, preview);
        if (open)
        {
            var spacing = ImGui.GetStyle().ItemSpacing;
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(spacing.X,
                spacing.Y + 5 * ImGui.GetIO().FontGlobalScale));
        }
        else
        {
            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor(5);
        }
        return open;
    }

    private static void EndStyledCombo()
    {
        ImGui.PopStyleVar();
        ImGui.EndCombo();
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(5);
    }

    protected override void OnStart(FeatureLifetime activation)
    {
        EnsureProfile();
        bridge = new NativeHotbarBridge(ReceiveDrop, () => FindDropArea() is not null);
        bridge.Start(activation);
        var ui = DalamudServices.PluginInterface.UiBuilder;
        activation.OnStopping(() => ui.Draw -= Draw);
        ui.Draw += Draw;
        var command = new CommandInfo((_, argument) => { if (!TryHandleCommand(argument)) DalamudServices.ChatGUI.Print(L("CommandUsage")); })
            { HelpMessage = L("CommandUsage"), AllowedInMacros = true };
        if (!DalamudServices.CommandManager.AddHandler("/draghotbar", command))
            throw new InvalidOperationException("/draghotbar 已由其他插件注册。");
        activation.OnStopping(() =>
        {
            if (DalamudServices.CommandManager.Commands.TryGetValue("/draghotbar", out var current) && ReferenceEquals(current, command))
                DalamudServices.CommandManager.RemoveHandler("/draghotbar");
        });
    }

    protected override void OnStop()
    {
        Save(); bridge = null; targets.Clear(); received.Clear(); replacing = null; CloseEditor(); owner = 0;
        // Keep unsaved shared settings in memory if a write failed while stopping.
        profileLoaded = dirty;
    }

    public override bool TryHandleCommand(string arguments)
    {
        if (!HotbarVisibilityCommands.TryParse(arguments, out var action, out var selector)) return false;
        EnsureProfile();
        if (owner == 0 || readOnly) { DalamudServices.ChatGUI.Print(L("CommandUnavailable")); return true; }
        if (!HotbarVisibilityCommands.Apply(profile, action, selector, out var index))
        { DalamudServices.ChatGUI.Print(L("InvalidBar", selector)); return true; }
        var visible = profile.Show && (index < 0 || profile.Bars[index].Visible);
        DalamudServices.ChatGUI.Print(L("VisibilityChanged", index < 0 ? L("Show") : BarLabel(profile.Bars[index]),
            L(visible ? "Shown" : "Hidden")));
        dirty = true; Save(); return true;
    }

    public override bool ResetSettings() { if (config.ShowHelp) return false; config.ShowHelp = true; return true; }

    private void EnsureProfile()
    {
        var service = DService.Instance();
        var id = service.ClientState.IsLoggedIn && service.PlayerState.IsLoaded ? service.PlayerState.ContentId : 0;
        if (owner == id) return;
        Save();
        owner = id;
        selected = 0; targets.Clear(); received.Clear(); replacing = null; CloseEditor(); localDragBar = null; movingBar = null;
        pendingImport = null; importOwner = 0; layoutBarId = "";
        // Character identity only guards game interaction and pending edits. It never selects
        // the persisted profile, and a character switch must not discard unsaved shared changes.
        if (id == 0 || profileLoaded) return;
        readOnly = false; recovering = false; saveError = "";
        try
        {
            profile = HotbarSharedProfiles.Load(DalamudServices.PluginInterface.GetPluginConfigDirectory(),
                id, out recovering, out var migratedFrom);
            profileLoaded = true;
            dirty = profile.NeedsUpgradeSave | PrepareEntryIcons();
            Save();
            if (migratedFrom is not null)
            {
                DalamudServices.PluginLog.Information("拖放热键栏：旧配置迁移到本地共享配置，来源 {Source}", migratedFrom);
                if (!dirty) Notify(L("Migrated", Path.GetFileName(migratedFrom)));
            }
        }
        catch (Exception error)
        { profileLoaded = false; readOnly = true; DalamudServices.PluginLog.Error(error, "拖放热键栏：共享配置加载或迁移失败"); }
    }

    private void Save()
    {
        if (!dirty || !profileLoaded || readOnly) return;
        try
        {
            PrepareEntryIcons();
            HotbarProfileStore.Save(ProfilePath, profile, HotbarSharedProfiles.Owner, recovering);
            dirty = false; recovering = false; saveError = "";
        }
        catch (Exception error) { saveError = error.Message; DalamudServices.PluginLog.Error(error, "拖放热键栏：配置保存失败"); }
    }

    private void Notify(string value) { notice = value; noticeUntil = Environment.TickCount64 + 4500; }

    private uint ResolveIcon(HotbarEntry entry)
    {
        uint commandIcon = 0;
        if (entry.Mode == HotbarEntryMode.Command)
        {
            try { commandIcon = macroIcons.Resolve(entry.Command, bridge); }
            catch (Exception error)
            {
                if (!iconWarningLogged) DalamudServices.PluginLog.Warning(error, "拖放热键栏：宏图标解析失败，使用备用图标");
                iconWarningLogged = true;
            }
        }
        var nativeIcon = entry.HasNative && bridge is not null && !entry.IconOverride ? bridge.CurrentIcon(entry) : 0;
        return HotbarPresentation.SelectIcon(entry, commandIcon, nativeIcon);
    }

    private bool PrepareEntryIcons()
    {
        var changed = false;
        foreach (var bar in profile.Bars)
            foreach (var entry in bar.Slots)
                if (entry.Mode == HotbarEntryMode.Command && entry.IconId == 0 && entry.Command.Length > 0)
                    changed |= HotbarPresentation.EnsureFallbackIcon(entry, ResolveIcon(entry), Random.Shared.Next);
        return changed;
    }

    public override bool DrawSettings()
    {
        EnsureProfile();
        nativeHotbarLocked = ReadNativeHotbarLock();
        var hostChanged = ImGui.Checkbox(L("ShowHelp"), ref config.ShowHelp);
        if (config.ShowHelp) { ImGui.TextWrapped(L("Help")); ImGui.TextWrapped(L("Help2")); }
        if (owner == 0) { ImGui.TextDisabled(L("Waiting")); return hostChanged; }
        if (readOnly) { ImGui.TextWrapped(L("Readonly")); return hostChanged; }
        if (recovering) ImGui.TextWrapped(L("Recovered"));
        if (saveError.Length > 0)
        {
            ImGui.TextWrapped(L("SaveError"));
            if (ImGui.Button(L("Retry"))) Save();
        }
        if (Check(L("Show"), profile.Show, v => profile.Show = v)) dirty = true;
        if (ImGui.Button(L("Add")) && profile.Bars.Count < HotbarRules.MaxBars)
        { profile.Bars.Add(new() { Name = $"拖放热键栏 {profile.Bars.Count + 1}", Y = 300 + profile.Bars.Count * 60 }); selected = profile.Bars.Count - 1; dirty = true; }
        selected = Math.Clamp(selected, 0, Math.Max(0, profile.Bars.Count - 1));
        if (profile.Bars.Count > 0)
        {
            ImGui.SameLine();
            ImGui.BeginDisabled(nativeHotbarLocked);
            if (ImGui.Button(L("Remove"))) ImGui.OpenPopup("NDHRemove");
            ImGui.EndDisabled();
            if (ImGui.BeginPopup("NDHRemove"))
            {
                ImGui.TextWrapped(L("RemoveConfirm", profile.Bars[selected].Slots.Count));
                ImGui.BeginDisabled(nativeHotbarLocked);
                if (ImGui.Button(L("ConfirmRemove")))
                { profile.Bars.RemoveAt(selected); selected = 0; CloseEditor(); replacing = null; dirty = true; ImGui.CloseCurrentPopup(); }
                ImGui.EndDisabled();
                ImGui.SameLine(); if (ImGui.Button(L("Cancel"))) ImGui.CloseCurrentPopup();
                ImGui.EndPopup();
            }
        }
        hostChanged |= DrawTransferSettings();
        selected = Math.Clamp(selected, 0, Math.Max(0, profile.Bars.Count - 1));
        if (profile.Bars.Count > 0)
        {
            if (BeginStyledCombo("##NDHBarSelect", BarLabel(profile.Bars[selected])))
            {
                for (var i = 0; i < profile.Bars.Count; i++)
                    if (ImGui.Selectable(BarLabel(profile.Bars[i]) + "##" + i, selected == i)) selected = i;
                EndStyledCombo();
            }
            DrawBarSettings(profile.Bars[selected]);
        }
        if (noticeUntil > Environment.TickCount64) ImGui.TextWrapped(notice);
        if (bridge is not null) ImGui.TextWrapped(L("Diagnostics", bridge.LastDrag));
        Save();
        return hostChanged;
    }

    private bool DrawTransferSettings()
    {
        var hostChanged = false;
        ImGui.BeginDisabled(profile.Bars.Count == 0);
        if (ImGui.Button(L("ExportCurrent"))) ExportSettings(selected);
        ImGui.SameLine();
        if (ImGui.Button(L("ExportAll"))) ExportSettings(null);
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button(L("ImportClipboard")))
        {
            pendingImport = null;
            try
            {
                pendingImport = HotbarTransfer.Parse(ImGui.GetClipboardText());
                importOwner = owner;
                importGlobalSettings = false;
            }
            catch (Exception error) { Notify(L("TransferError", error.Message)); }
        }
        if (pendingImport is null) return hostChanged;
        ImGui.Separator();
        ImGui.TextWrapped(L("ImportPreview", pendingImport.Bars.Count,
            pendingImport.Bars.Sum(bar => bar.SlotCount),
            pendingImport.Bars.Sum(bar => bar.Slots.Count - bar.SlotCount)));
        foreach (var bar in pendingImport.Bars)
            ImGui.TextUnformatted($"{bar.Name} · {bar.Columns} × {bar.Rows} · "
                + $"{bar.Slots.Count(slot => !slot.IsEmpty)} 个已配置格子");
        ImGui.TextWrapped(L("ImportNotice"));
        if (pendingImport.ShowAllBars.HasValue || pendingImport.ShowHelp.HasValue)
            ImGui.Checkbox(L("ImportGlobal"), ref importGlobalSettings);
        var exceedsLimit = profile.Bars.Count + pendingImport.Bars.Count > HotbarRules.MaxBars;
        if (exceedsLimit) ImGui.TextWrapped(L("ImportLimit", profile.Bars.Count));
        ImGui.BeginDisabled(exceedsLimit || nativeHotbarLocked || importOwner != owner);
        if (ImGui.Button(L("ConfirmImport")))
        {
            try
            {
                var imported = pendingImport;
                selected = HotbarTransfer.Append(profile, HotbarSharedProfiles.Owner, imported);
                if (importGlobalSettings)
                {
                    if (imported.ShowAllBars is { } show) profile.Show = show;
                    if (imported.ShowHelp is { } help)
                    { hostChanged = config.ShowHelp != help; config.ShowHelp = help; }
                }
                targets.Clear(); received.Clear(); replacing = null; CloseEditor();
                localDragBar = null; localDragIndex = -1; layoutBarId = "";
                dirty = true; Save(); pendingImport = null;
                Notify(L("Imported", imported.Bars.Count));
            }
            catch (Exception error) { Notify(L("TransferError", error.Message)); }
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button(L("Cancel") + "##NDHImport")) pendingImport = null;
        ImGui.Separator();
        return hostChanged;
    }

    private void ExportSettings(int? index)
    {
        try
        {
            ImGui.SetClipboardText(HotbarTransfer.Export(profile, index, config.ShowHelp, Version));
            Notify(L("Exported"));
        }
        catch (Exception error) { Notify(L("TransferError", error.Message)); }
    }

    private void DrawBarSettings(DragHotbar bar)
    {
        var name = bar.Name;
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(L("Name"));
        ImGui.SameLine();
        ImGui.SetNextItemWidth(260);
        if (ImGui.InputText("##NDHBarName", ref name, 128)) { bar.Name = name; dirty = true; }
        if (Check(L("Visible"), bar.Visible, v => bar.Visible = v)) dirty = true;
        ImGui.SameLine();
        if (Check(L("HideEmpty"), bar.HideEmpty, v => bar.HideEmpty = v)) dirty = true;
        ImGui.SameLine(); ImGui.TextDisabled(L("NativeLock", L(nativeHotbarLocked ? "Locked" : "Unlocked")));
        ImGui.AlignTextToFramePadding(); ImGui.TextUnformatted(L("Opacity")); ImGui.SameLine();
        ImGui.SetNextItemWidth(Math.Max(80, ImGui.GetContentRegionAvail().X));
        var opacity = bar.Opacity;
        if (ImGui.SliderFloat("##NDHOpacity", ref opacity, 0f, 1f, "%.2f")) { bar.Opacity = opacity; dirty = true; }
        ImGui.AlignTextToFramePadding(); ImGui.TextUnformatted(L("Scale")); ImGui.SameLine();
        ImGui.SetNextItemWidth(Math.Max(80, ImGui.GetContentRegionAvail().X));
        var scale = bar.Scale;
        if (ImGui.SliderFloat("##NDHScale", ref scale, 0.5f, 2.5f, "%.2f")) { bar.Scale = scale; dirty = true; }
        if (layoutBarId != bar.Id)
        { layoutBarId = bar.Id; layoutColumns = bar.Columns; layoutRows = bar.Rows; }
        ImGui.AlignTextToFramePadding(); ImGui.TextUnformatted(L("Layout"));
        ImGui.AlignTextToFramePadding(); ImGui.TextUnformatted(L("Horizontal")); ImGui.SameLine();
        ImGui.SetNextItemWidth(110);
        ImGui.InputInt("##NDHColumns", ref layoutColumns, 1, 10);
        ImGui.SameLine(); ImGui.TextUnformatted("×"); ImGui.SameLine();
        ImGui.AlignTextToFramePadding(); ImGui.TextUnformatted(L("Vertical")); ImGui.SameLine();
        ImGui.SetNextItemWidth(110);
        ImGui.InputInt("##NDHRows", ref layoutRows, 1, 10);
        var validLayout = HotbarRules.IsValidLayout(layoutColumns, layoutRows);
        if (!validLayout) ImGui.TextWrapped(L("LayoutInvalid"));
        ImGui.BeginDisabled(!validLayout || (layoutColumns == bar.Columns && layoutRows == bar.Rows));
        if (ImGui.Button(L("ApplyLayout")))
        {
            HotbarRules.Resize(bar, layoutColumns, layoutRows);
            targets.Clear(); received.Clear(); replacing = null; CloseEditor(); localDragBar = null; localDragIndex = -1;
            reposition.Add(bar.Id); dirty = true;
        }
        ImGui.EndDisabled();
        ImGui.SameLine(); ImGui.TextUnformatted(L("LayoutCount", bar.Columns, bar.Rows, bar.SlotCount));
        if (bar.Slots.Count > bar.SlotCount)
            ImGui.TextWrapped(L("HiddenSlots", bar.Slots.Count - bar.SlotCount,
                bar.Slots.Skip(bar.SlotCount).Count(slot => !slot.IsEmpty)));
        if (ImGui.Button(L("Recenter")))
        { var view = ImGui.GetMainViewport(); var size = BarSize(bar); var pos = NativeHotbarLayout.SetPosition(view.WorkPos + Vector2.Max(Vector2.Zero, view.WorkSize - size) * 0.5f, view.WorkPos, view.WorkSize, size, out var ax, out var ay); bar.X = pos.X; bar.Y = pos.Y; bar.AnchorX = ax; bar.AnchorY = ay; reposition.Add(bar.Id); dirty = true; }
        var buttonWidth = 48f * ImGui.GetIO().FontGlobalScale;
        var editorColumns = Math.Min(bar.Columns, Math.Max(1,
            (int)((ImGui.GetContentRegionAvail().X + ImGui.GetStyle().ItemSpacing.X) / (buttonWidth + ImGui.GetStyle().ItemSpacing.X))));
        ImGui.BeginDisabled(nativeHotbarLocked);
        for (var i = 0; i < bar.SlotCount; i++)
        {
            ImGui.PushID(i);
            if (i % editorColumns != 0) ImGui.SameLine();
            if (ImGui.Button((i + 1).ToString(), new Vector2(buttonWidth, ImGui.GetFrameHeight()))) OpenEditor(bar, i);
            ImGui.PopID();
        }
        ImGui.EndDisabled();
    }

    private static bool Check(string label, bool original, Action<bool> setter)
    { var value = original; if (!ImGui.Checkbox(label, ref value)) return false; setter(value); return true; }

    private sealed class OmniGlassStyleScope : IDisposable
    {
        public OmniGlassStyleScope()
        {
            ImGui.PushStyleColor(ImGuiCol.WindowBg, 0xD044403A);
            ImGui.PushStyleColor(ImGuiCol.PopupBg, 0xE04B4742);
            ImGui.PushStyleColor(ImGuiCol.Border, 0xC0C5BEB4);
            ImGui.PushStyleColor(ImGuiCol.FrameBg, 0xB044403A);
            ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, 0xD05A554E);
            ImGui.PushStyleColor(ImGuiCol.FrameBgActive, 0xD03D3934);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 9);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1);
        }

        public void Dispose()
        {
            ImGui.PopStyleVar(3);
            ImGui.PopStyleColor(6);
        }
    }

    private void Draw()
    {
        try
        {
            EnsureProfile();
            nativeHotbarLocked = ReadNativeHotbarLock();
            targets.Clear();
            editorVisible = false;
            targetsAt = Environment.TickCount64;
            // Derive coordinates from the game client origin, not ImGui's mouse sample: the latter
            // can lag behind the OS cursor while the game owns a native drag.
            if (NativeHotbarBridge.TryGetGameClientOrigin(out var origin))
                cursorOffset = ImGui.GetMainViewport().Pos - new Vector2(origin.X, origin.Y);
            if (owner == 0 || readOnly) return;
            ProcessDrops();
            var dragging = bridge?.IsDragging == true;
            var supported = dragging && bridge!.Preview();
            if (profile.Show)
                foreach (var bar in profile.Bars.Where(b => b.Visible)) DrawBar(bar, dragging, supported);
            DrawEditor();
            DrawReplacement();
            bridge?.PollRelease();
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left)) { localDragBar = null; localDragIndex = -1; movingBar = null; }
            if (dirty && !ImGui.IsMouseDown(ImGuiMouseButton.Left)) Save();
        }
        catch (Exception error) { targets.Clear(); DalamudServices.PluginLog.Error(error, "拖放热键栏：绘制失败"); }
    }

    private void DrawBar(DragHotbar bar, bool nativeDragging, bool supported)
    {
        var view = ImGui.GetMainViewport();
        var expected = BarSize(bar);
        var hadAnchor = bar.AnchorX.HasValue && bar.AnchorY.HasValue;
        if (replacing is null && ReferenceEquals(movingBar, bar) && !nativeDragging && ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            var moved = NativeHotbarLayout.SetPosition(new Vector2(bar.X, bar.Y) + ImGui.GetIO().MouseDelta,
                view.WorkPos, view.WorkSize, expected, out var movedAnchorX, out var movedAnchorY);
            bar.X = moved.X; bar.Y = moved.Y; bar.AnchorX = movedAnchorX; bar.AnchorY = movedAnchorY;
            reposition.Add(bar.Id); dirty = true;
        }
        var pos = NativeHotbarLayout.ResolvePosition(bar.X, bar.Y, bar.AnchorX, bar.AnchorY,
            view.WorkPos, view.WorkSize, expected, out var anchorX, out var anchorY);
        var forcePosition = !hadAnchor || reposition.Remove(bar.Id) || Vector2.DistanceSquared(pos, new(bar.X, bar.Y)) > 0.1f;
        if (!hadAnchor) dirty = true;
        bar.AnchorX = anchorX; bar.AnchorY = anchorY;
        ImGui.SetNextWindowPos(pos, forcePosition ? ImGuiCond.Always : ImGuiCond.Appearing);
        // Keep the window active at zero opacity so its fully visible icons remain clickable.
        ImGui.SetNextWindowBgAlpha(bar.Opacity);
        var windowBorder = ImGui.GetStyle().Colors[(int)ImGuiCol.Border];
        windowBorder.W *= bar.Opacity;
        ImGui.PushStyleColor(ImGuiCol.Border, windowBorder);
        var flags = ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.AlwaysAutoResize
            | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoFocusOnAppearing
            | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoMove;
        var drawContents = ImGui.Begin(BarLabel(bar) + "###NativeDragHotbar-" + bar.Id, flags);
        try
        {
            if (drawContents)
            {
                if (replacing is null && !nativeDragging && ImGui.GetIO().KeyAlt
                    && ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem) && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) movingBar = bar;
                var position = ImGui.GetWindowPos();
                if (Vector2.DistanceSquared(position, new(bar.X, bar.Y)) > 0.1f)
                { bar.X = position.X; bar.Y = position.Y; bar.AnchorX = anchorX; bar.AnchorY = anchorY; dirty = true; }
                var visible = 0;
                var size = Math.Clamp(40f * bar.Scale * ImGui.GetIO().FontGlobalScale, 20, 120);
                for (var i = 0; i < bar.SlotCount; i++)
                {
                    if (bar.HideEmpty && !nativeDragging && localDragBar is null && bar.Slots[i].IsEmpty) continue;
                    if (visible++ % bar.Columns != 0) ImGui.SameLine(0, 4);
                    DrawSlot(bar, i, size, nativeDragging, supported);
                }
                if (visible == 0) ImGui.TextDisabled(L("Empty"));
                if (noticeUntil > Environment.TickCount64) ImGui.TextWrapped(notice);
            }
        }
        finally { ImGui.End(); ImGui.PopStyleColor(); }
    }

    private static Vector2 BarSize(DragHotbar bar)
    {
        var size = Math.Clamp(40f * bar.Scale * ImGui.GetIO().FontGlobalScale, 20, 120);
        return new Vector2(bar.Columns * (size + 4) + 12, bar.Rows * (size + 4) + 12);
    }

    private void DrawSlot(DragHotbar bar, int index, float size, bool nativeDragging, bool supported)
    {
        ImGui.PushID(index);
        var entry = bar.Slots[index];
        var min = ImGui.GetCursorScreenPos(); var max = min + new Vector2(size);
        var clicked = ImGui.InvisibleButton("##slot", new(size));
        var hovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
        var list = ImGui.GetWindowDrawList();
        var border = hovered ? (nativeDragging ? (supported ? 0xFF6EE6A5u : 0xFF6B6BEEu) : 0xFFDDDDDDu) : 0xFF555555u;
        var tints = HotbarPresentation.SlotTints(bar.Opacity, ImGui.GetStyle().Alpha, border);
        list.AddRectFilled(min, max, tints.Surface, 3);
        var icon = ResolveIcon(entry);
        if (icon > 0 && ImageHelper.GetGameIcon(icon) is { } texture)
            list.AddImage(texture.Handle, min + new Vector2(1), max - new Vector2(1), Vector2.Zero, Vector2.One,
                tints.Image);
        list.AddRect(min, max, tints.Frame, 3, ImDrawFlags.None, hovered ? 2 : 1);
        if (replacing is null)
            targets.Add(new(bar, index, min, max, entry));
        if (hovered && !nativeDragging && localDragBar is null)
        {
            using var theme = new OmniGlassStyleScope();
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(entry.IsEmpty ? L("Empty") : (entry.Tooltip.Length > 0 ? entry.Tooltip : entry.Name));
            if (entry.Mode == HotbarEntryMode.Command && entry.Command.Length > 0) ImGui.TextUnformatted(entry.Command);
            ImGui.EndTooltip();
        }
        if (replacing is null && hovered && ImGui.IsMouseReleased(ImGuiMouseButton.Right) && !nativeHotbarLocked && !nativeDragging) OpenEditor(bar, index);
        if (replacing is null && !nativeHotbarLocked && !nativeDragging && !ImGui.GetIO().KeyAlt && movingBar is null && ImGui.BeginDragDropSource())
        {
            if (ImGui.SetDragDropPayload("NDH_LOCAL_SLOT", [])) { localDragBar = bar; localDragIndex = index; }
            ImGui.TextUnformatted(entry.Name.Length > 0 ? entry.Name : L("Slot", index + 1));
            ImGui.EndDragDropSource();
        }
        if (replacing is null && !nativeHotbarLocked && !nativeDragging && ImGui.BeginDragDropTarget())
        {
            var payload = ImGui.AcceptDragDropPayload("NDH_LOCAL_SLOT");
            if (!payload.IsNull && localDragBar is not null && localDragIndex >= 0 && !nativeHotbarLocked
                && !(ReferenceEquals(bar, localDragBar) && index == localDragIndex))
            {
                (localDragBar.Slots[localDragIndex], bar.Slots[index]) = (bar.Slots[index], localDragBar.Slots[localDragIndex]);
                localDragBar = null; localDragIndex = -1; dirty = true;
            }
            ImGui.EndDragDropTarget();
        }
        if (clicked && !nativeDragging && localDragBar is null && movingBar is null && !ImGui.GetIO().KeyAlt
            && Environment.TickCount64 - lastNativeDrop > 350 && !editorOpen && replacing is null)
            Execute(entry);
        ImGui.PopID();
    }

    private DropArea? FindDropArea()
    {
        if (owner == 0 || readOnly || !profile.Show || replacing is not null
            || Environment.TickCount64 - targetsAt > 150 || !NativeHotbarBridge.GetCursorPos(out var point)) return null;
        var pos = new Vector2(point.X, point.Y) + cursorOffset;
        return targets.LastOrDefault(t => t.Bar.Visible && profile.Bars.Contains(t.Bar)
            && (t.Editing || !editorVisible || pos.X < editorMin.X || pos.X >= editorMax.X || pos.Y < editorMin.Y || pos.Y >= editorMax.Y)
            && pos.X >= t.Min.X && pos.X < t.Max.X && pos.Y >= t.Min.Y && pos.Y < t.Max.Y);
    }

    private bool ReceiveDrop(HotbarEntry entry)
    {
        var target = FindDropArea();
        if (target is null || received.Count > 0) return false;
        // Dropping on the slot currently being edited updates its draft, preserving Save/Cancel.
        var editing = target.Editing || (editorOpen && ReferenceEquals(editBar, target.Bar) && editIndex == target.Index && draft is not null);
        received.Enqueue(new(owner, target.Bar, target.Index, editing ? draft! : target.Before, entry.Copy(), editing, target.MacroInsertion));
        lastNativeDrop = Environment.TickCount64;
        return true;
    }

    private void ProcessDrops()
    {
        while (received.TryDequeue(out var request))
        {
            if (!ValidRequest(request)) continue;
            if (request.MacroInsertion)
            {
                AppendMacroDrop(request.Entry);
                continue;
            }
            if (!request.Before.IsEmpty) replacing = request;
            else ApplyDrop(request);
        }
    }

    private void AppendMacroDrop(HotbarEntry entry)
    {
        if (draft is null || nativeHotbarLocked) return;
        try
        {
            var emoteCommand = entry.NativeType == 6
                ? DalamudServices.DataManager.GetExcelSheet<Emote>().GetRowOrDefault(entry.NativeId)
                    ?.TextCommand.ValueNullable?.Command.ToString()
                : null;
            var text = HotbarRules.MacroDropText(entry, emoteCommand);
            if (text is null) { Notify(L("MacroDropUnsupported")); return; }
            if (!HotbarRules.TryAppendMacroCommand(draft.Command, text, out var updated))
            { Notify(L("MacroDropFull")); return; }
            draft.Command = updated;
            refreshCommandBuffer = true;
            Notify(L("MacroDropAdded", text));
        }
        catch (Exception error)
        {
            DalamudServices.PluginLog.Warning(error, "拖放热键栏：读取拖入动作的宏文本失败");
            Notify(L("MacroDropUnsupported"));
        }
    }

    private bool ValidRequest(DropRequest request) => owner == request.Owner && profile.Bars.Contains(request.Bar)
        && request.Index >= 0 && request.Index < request.Bar.SlotCount
        && (!request.MacroInsertion || (!nativeHotbarLocked && draft?.Mode == HotbarEntryMode.Command))
        && (request.Editing
            ? editorOpen && ReferenceEquals(editBar, request.Bar) && editIndex == request.Index && ReferenceEquals(draft, request.Before)
            : ReferenceEquals(request.Bar.Slots[request.Index], request.Before));
    private void ApplyDrop(DropRequest request)
    {
        if (!ValidRequest(request)) return;
        if (request.Editing)
        { draft = request.Entry.Copy(); Notify(L("DraftCopied", request.Entry.Name)); return; }
        request.Bar.Slots[request.Index] = request.Entry.Copy(); dirty = true; Save(); Notify(L("Copied", request.Entry.Name));
    }

    private void DrawReplacement()
    {
        if (replacing is null) return;
        if (!ValidRequest(replacing)) { replacing = null; return; }
        using var theme = new OmniGlassStyleScope();
        var open = true;
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(HotbarPresentation.ReplacementCenter(editorVisible, editorMin, editorMax,
            viewport.WorkPos, viewport.WorkSize), ImGuiCond.Always, new Vector2(0.5f));
        ImGui.SetNextWindowSize(new(380, 0), ImGuiCond.FirstUseEver);
        // The editor is input-blocked while this request is pending. Focus also brings this window
        // to the display front each frame instead of relying on draw submission order alone.
        ImGui.SetNextWindowFocus();
        if (ImGui.Begin(L("ReplaceTitle"), ref open, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoMove))
        {
            ImGui.TextWrapped(L("ReplaceText", replacing.Before.Name, replacing.Entry.Name));
            if (ImGui.Button(L("Replace"))) { ApplyDrop(replacing); replacing = null; }
            ImGui.SameLine(); if (ImGui.Button(L("Cancel"))) replacing = null;
        }
        ImGui.End(); if (!open) replacing = null;
    }

    private void OpenEditor(DragHotbar bar, int index)
    { editBar = bar; editIndex = index; editOriginal = bar.Slots[index]; draft = editOriginal.Copy(); editorOpen = true; refreshCommandBuffer = false; }
    private void CloseEditor() { editorOpen = false; editorVisible = false; editBar = null; editOriginal = null; draft = null; refreshCommandBuffer = false; }

    private void DrawEditor()
    {
        if (!editorOpen || draft is null || editBar is null) return;
        if (!profile.Bars.Contains(editBar)) { CloseEditor(); return; }
        using var theme = new OmniGlassStyleScope();
        ImGui.SetNextWindowSize(new(500, 420), ImGuiCond.FirstUseEver);
        var open = true;
        var editorFlags = ImGuiWindowFlags.NoCollapse;
        if (replacing is not null) editorFlags |= ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize;
        if (ImGui.Begin(L("Editor"), ref open, editorFlags))
        {
            editorVisible = true;
            editorMin = ImGui.GetWindowPos(); editorMax = editorMin + ImGui.GetWindowSize();
            ImGui.TextUnformatted(BarLabel(editBar) + " · " + L("Slot", editIndex + 1));
            var min = ImGui.GetCursorScreenPos();
            var dropSize = new Vector2(Math.Max(1, ImGui.GetContentRegionAvail().X), 54 * ImGui.GetIO().FontGlobalScale);
            ImGui.InvisibleButton("##NDHEditDrop", dropSize);
            var max = min + dropSize;
            var drawList = ImGui.GetWindowDrawList();
            drawList.AddRectFilled(min, max, ImGui.GetColorU32(ImGuiCol.FrameBg), 4);
            drawList.AddRect(min, max, ImGui.GetColorU32(ImGuiCol.Border), 4);
            var textAt = min + new Vector2(8, 8);
            var previewIcon = ResolveIcon(draft);
            if (previewIcon > 0 && ImageHelper.GetGameIcon(previewIcon) is { } previewTexture)
            {
                var iconSize = new Vector2(dropSize.Y - 8);
                drawList.AddImage(previewTexture.Handle, min + new Vector2(4), min + new Vector2(4) + iconSize);
                textAt.X += iconSize.X + 4;
            }
            drawList.AddText(textAt, ImGui.GetColorU32(ImGuiCol.Text), L("EditorDrop"));
            if (replacing is null)
                targets.Add(new(editBar, editIndex, min, max, draft, true));
            if (draft.HasNative) ImGui.TextWrapped(L("NativePreserved", draft.Name, draft.NativeType, draft.NativeId));
            ImGui.BeginDisabled(nativeHotbarLocked);
            if (BeginStyledCombo(L("Mode"), L(draft.Mode == HotbarEntryMode.Native ? "Native" : "Command")))
            {
                if (draft.HasNative && ImGui.Selectable(L("Native"), draft.Mode == HotbarEntryMode.Native)) draft.Mode = HotbarEntryMode.Native;
                if (ImGui.Selectable(L("Command"), draft.Mode == HotbarEntryMode.Command)) draft.Mode = HotbarEntryMode.Command;
                EndStyledCombo();
            }
            if (!draft.HasNative) draft.Mode = HotbarEntryMode.Command;
            var name = draft.Name; if (ImGui.InputText(L("Name"), ref name, 256)) draft.Name = name;
            var tooltip = draft.Tooltip; if (ImGui.InputText(L("Tooltip"), ref tooltip, 1024)) draft.Tooltip = tooltip;
            var icon = (int)draft.IconId;
            if (ImGui.InputInt(L("Icon"), ref icon)) { draft.IconId = (uint)Math.Clamp(icon, 0, 10_000_000); draft.IconOverride = draft.IconId > 0; }
            if (ImGui.Button(L("PickIcon")))
            {
                var edited = draft;
                OpenIconBrowser(value => { if (ReferenceEquals(draft, edited)) { edited.IconId = value; edited.IconOverride = true; } });
            }
            if (draft.HasNative)
            { ImGui.SameLine(); if (ImGui.Button(L("RestoreIcon"))) { draft.IconId = bridge?.CurrentIcon(draft) ?? draft.IconId; draft.IconOverride = false; } }
            ImGui.TextWrapped(L("IconPriority"));
            if (draft.Mode == HotbarEntryMode.Command)
            {
                ImGui.TextWrapped(L("CommandHelp"));
                ImGui.TextWrapped(L("MacroDropHelp"));
                var command = draft.Command;
                if (refreshCommandBuffer) ImGui.SetKeyboardFocusHere();
                if (ImGui.InputTextMultiline("##command", ref command, 8192, new(-1, 130),
                    ImGuiInputTextFlags.CallbackAlways, RefreshCommandText))
                {
                    if (command.Length <= 2048) draft.Command = command;
                    else refreshCommandBuffer = true;
                }
                if (replacing is null && !nativeHotbarLocked)
                    targets.Add(new(editBar, editIndex, ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), draft, true, true));
                if (noticeUntil > Environment.TickCount64) ImGui.TextWrapped(notice);
            }
            ImGui.EndDisabled();
            if (ImGui.Button(L("Save")))
            {
                if (ReferenceEquals(editBar.Slots[editIndex], editOriginal)) { editBar.Slots[editIndex] = draft.Copy(); dirty = true; Save(); }
                CloseEditor();
            }
            ImGui.SameLine(); if (ImGui.Button(L("Cancel"))) CloseEditor();
            ImGui.SameLine(); ImGui.BeginDisabled(nativeHotbarLocked);
            if (ImGui.Button(L("Clear")))
            {
                if (editBar is not null && ReferenceEquals(editBar.Slots[editIndex], editOriginal))
                { editBar.Slots[editIndex] = new(); dirty = true; Save(); }
                CloseEditor();
            }
            ImGui.EndDisabled();
            var shade = HotbarPresentation.EditorShade(replacing is not null);
            if (shade != 0)
                ImGui.GetWindowDrawList().AddRectFilled(editorMin, editorMax, shade, ImGui.GetStyle().WindowRounding);
        }
        ImGui.End(); if (!open) CloseEditor();
    }

    private int RefreshCommandText(ImGuiInputTextCallbackDataPtr data)
    {
        if (!refreshCommandBuffer || draft is null) return 0;
        data.DeleteChars(0, data.BufTextLen);
        data.InsertChars(0, draft.Command);
        data.CursorPos = data.BufTextLen;
        data.SelectionStart = data.SelectionEnd = data.CursorPos;
        refreshCommandBuffer = false;
        return 0;
    }

    private void Execute(HotbarEntry entry)
    {
        try
        {
            if (!NativeHotbarBridge.IsGameForeground()) return;
            if (entry.HasNative) bridge?.Execute(entry);
            if (entry.Mode == HotbarEntryMode.Command)
                foreach (var command in HotbarRules.Commands(entry.Command))
                {
                    if (HotbarPresentation.IsIconLine(command)) continue;
                    if (!DalamudServices.CommandManager.ProcessCommand(command)) ChatManager.Instance().SendMessage(command);
                }
        }
        catch (Exception error) { DalamudServices.PluginLog.Error(error, "拖放热键栏：执行失败"); Notify(L("NativeError")); }
    }
}
