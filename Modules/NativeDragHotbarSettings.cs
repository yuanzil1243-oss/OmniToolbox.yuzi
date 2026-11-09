using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using OmniToolbox.SuperHotbarInterop;

namespace OmniToolbox.NativeDragHotbarLocal;

public sealed partial class NativeDragHotbar
{
    private int superSettingsTab = -1, superRequestedSettingsTab = -1;
    private bool superSettingsExpanded, sharedSettingsExpanded;
    private string superRestoreResult = "", superRestoreBackupPath = "";
    private static readonly string[] SuperSettingsTabs =
        ["接管", "栏位与布局", "快捷键", "备份与导入", "还原到游戏", "扩展栏", "操作说明"];
    private static readonly int[] SuperSettingsTabOrder = [0, 1, 2, 5, 3, 4, 6];
    private static readonly (string Command, string Description)[] SuperHelpCommands = [
        ("/superhotbar toggle", "显示或隐藏扩展栏"),
        ("/superhotbar takeover toggle", "显示或隐藏接管栏"),
        ("/superhotbar native", "退出接管，使用游戏热键栏"),
    ];

    private void CollapseSettingsBars() => superSettingsExpanded = sharedSettingsExpanded = false;

    private bool DrawSuperSettings()
    {
        superLastSettings = Environment.TickCount64;
        ImGui.TextUnformatted($"热键栏工坊 {Version}");
        ImGui.SameLine();
        ImGui.TextDisabled(SuperActive ? "接管中" : super is { Enabled: true } ? "接管暂时暂停" : "未接管");
        if (super is { Enabled: true } || superNativeDrawSuppressed)
        {
            if (ImGui.Button("退出接管，使用游戏热键栏")) ExitSuperTakeover();
        }
        var hostChanged = false;
        var requested = superRequestedSettingsTab;
        superRequestedSettingsTab = -1;
        if (ImGui.BeginTabBar("##SuperHotbarSettings", ImGuiTabBarFlags.FittingPolicyScroll))
        {
            try
            {
                foreach (var tab in SuperSettingsTabOrder)
                {
                    var flags = requested == tab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
                    if (!ImGui.BeginTabItem(SuperSettingsTabs[tab], flags)) continue;
                    try
                    {
                        EnterSuperSettingsTab(tab);
                        ImGui.Spacing();
                        switch (tab)
                        {
                            case 0: DrawSuperOverviewSettings(); break;
                            case 1: DrawSuperAppearanceSettings(); break;
                            case 2: DrawSuperBindingSettings(); break;
                            case 3: DrawSuperArchiveSettings(); break;
                            case 4: DrawSuperRestoreSettings(); break;
                            case 5: hostChanged |= DrawSharedBarSettings(); break;
                            case 6: hostChanged |= DrawSuperHelpSettings(); break;
                        }
                    }
                    finally { ImGui.EndTabItem(); }
                }
            }
            finally { ImGui.EndTabBar(); }
        }
        ImGui.Spacing();
        if (superSettingsTab != 4 || superStatus != superRestoreResult) ImGui.TextWrapped(superStatus);
        SaveSuper(false);
        hostChanged |= superHostResetDirty; superHostResetDirty = false;
        return hostChanged;
    }

    private void EnterSuperSettingsTab(int tab)
    {
        if (superSettingsTab == tab) return;
        if (superSettingsTab == 2)
        {
            superRecording = -1; superBindingDraft = null;
            superEdges.BlockHeld(NativeHotbarBridge.IsKeyDown);
        }
        if (superSettingsTab == 3) { ClearSuperImport(); superResetConfirm = 0; }
        if (superSettingsTab == 4) { restoreConfirm = false; superRestorePreview = null; }
        superSettingsTab = tab;
    }

    [MemberNotNullWhen(true, nameof(super))]
    private bool RequireSuperSettingsArchive()
    {
        if (owner != 0 && super is not null) return true;
        ImGui.TextWrapped(owner == 0 ? "请先登录角色。" : "请先到“备份与导入”读取原生配置，或导入已有备份。");
        return false;
    }

    private void BeginSuperNativeRead()
    {
        ClearSuperImport();
        try { superImport = SuperNativeArchive.Capture(owner); MarkWorkshopOperation(); }
        catch (Exception error) { superStatus = "读取原生配置失败：" + error.Message; }
    }

    private void ClearSuperImport()
    {
        superImport = null;
        pendingImport = null; ClearWorkshopOperations();
    }

    private void DrawSuperOverviewSettings()
    {
        if (owner == 0) { ImGui.TextWrapped("请先登录角色。本机所有角色共用当前选中的配置。"); return; }
        ImGui.TextWrapped("在“备份与导入”选择配置；首次使用可读取游戏配置，检查布局与快捷键后启用接管。切换角色继续使用当前选择。");
        if (super is null)
        {
            if (ImGui.Button("读取原生配置…")) { BeginSuperNativeRead(); superRequestedSettingsTab = 3; }
            return;
        }
        var enabled = super.Enabled;
        if (ImGui.Checkbox("启用热键栏接管", ref enabled))
        {
            if (!enabled) ExitSuperTakeover();
            else
            {
                super.Enabled = true; super.Show = true; superFault = false; superDirty = true;
                superGroup = -1; superReady = false; RestoreSuperVisibility();
                superEdges.BlockHeld(NativeHotbarBridge.IsKeyDown); SaveSuper(true);
                if (!superDirty) superStatus = "接管已启用。";
            }
        }
        var show = super.Show;
        if (ImGui.Checkbox("显示接管栏", ref show)) { super.Show = show; superDirty = true; }
        var hide = super.HideNative;
        if (ImGui.Checkbox("接管时隐藏游戏原有热键栏", ref hide))
        { super.HideNative = hide; superDirty = true; if (!hide) RestoreSuperVisibility(); }
        var locked = super.Locked;
        if (ImGui.Checkbox("锁定格子和栏位", ref locked)) SetSuperLocked(locked);
        ImGui.TextWrapped("锁定后不能拖换、编辑或移动，仍可使用技能。也可点击主栏左下角的锁切换。");
        var builtIn = super.BuiltInRepeat;
        if (ImGui.Checkbox("按住快捷键连续使用", ref builtIn))
        { super.BuiltInRepeat = builtIn; superDirty = true; builtInRepeat.Clear(); superEdges.BlockHeld(NativeHotbarBridge.IsKeyDown); }
        var cps = super.RepeatCps;
        if (ImGui.SliderFloat("每秒连点次数", ref cps, 1, 30, "%.1f"))
        { super.RepeatCps = cps; superDirty = true; builtInRepeat.Clear(); superEdges.BlockHeld(NativeHotbarBridge.IsKeyDown); }
        ImGui.TextWrapped("使用“快捷键”页设置的按键；聊天、切出游戏或编辑界面时暂停，松开后再按即可继续。");
        var repeat = super.AllowKeyRepeater;
        if (ImGui.Checkbox("兼容“按键连点”模块", ref repeat))
        { super.AllowKeyRepeater = repeat; superDirty = true; superEdges.BlockHeld(NativeHotbarBridge.IsKeyDown); }
        ImGui.TextWrapped("使用本页连点时无需其他模块；关闭本页连点后，才会使用外部连点。");
        ImGui.Separator();
        ImGui.TextWrapped("隐藏接管栏后，快捷键仍可使用。退出接管会恢复游戏热键栏；要把模块内容写回游戏，请到“还原到游戏”。");
    }

    private void DrawSuperArchiveSettings() => DrawWorkshopArchiveSettings();

    private bool DrawSuperHelpSettings()
    {
        ImGui.TextWrapped(L("Help")); ImGui.TextWrapped(L("Help2"));
        ImGui.TextWrapped("扩展栏不受游戏热键栏锁和接管锁影响，仍可拖入、拖换格子和编辑；Alt + 左键可移动整栏。");
        ImGui.TextWrapped("技能、道具和可识别的宏会显示游戏原生介绍；显示位置跟随游戏帮助设置。");
        ImGui.TextWrapped("格子上的 c、a、s 分别表示 Ctrl、Alt、Shift；冷却中的充能技能显示下一层恢复时间。");
        ImGui.TextWrapped("编辑游戏界面布局时，接管栏暂停操作。十字热键栏和特殊临时栏仍由游戏显示。");
        if (superGeometryWarning) ImGui.TextWrapped("暂时无法读取游戏栏位布局，正在使用已保存的位置。");
        ImGui.Separator();
        var changed = ImGui.Checkbox(L("ShowCommandMessages"), ref config.ShowCommandMessages);
        ImGui.TextWrapped("在聊天栏显示命令执行结果和错误提示；关闭后命令照常执行。");
        ImGui.TextUnformatted("常用命令");
        ImGui.TextDisabled("点击左侧指令即可复制。");
        if (ImGui.BeginTable("##super-commands", 2, ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("指令", ImGuiTableColumnFlags.WidthStretch, 1);
            ImGui.TableSetupColumn("作用", ImGuiTableColumnFlags.WidthStretch, 1);
            foreach (var (command, description) in SuperHelpCommands)
                if (DrawSuperHelpCommand(command, description)) ImGui.SetClipboardText(command);
            ImGui.EndTable();
        }
        ImGui.TextWrapped("前两条命令可追加 #编号或栏名，例如 /superhotbar toggle #2。用 on 或 off 代替 toggle 可指定显示或隐藏。");
        if (superFault && ImGui.Button("重试接管")) { superFault = false; superGroup = -1; }
        return changed;
    }

    private static bool DrawSuperHelpCommand(string command, string description)
    {
        ImGui.TableNextRow(); ImGui.TableSetColumnIndex(0);
        var copy = ImGui.Selectable(command);
        ImGui.TableSetColumnIndex(1); ImGui.TextWrapped(description);
        return copy;
    }

    // Same single-column card placement as the achievement tracker: one expanded editor.
    private static void DrawHotbarSettingsCard(string id, string title, bool expanded,
        Action toggle, Action drawSettings)
    {
        ImGui.PushID(id);
        try
        {
            if (!ImGui.BeginTable("##hotbar-card", 1,
                ImGuiTableFlags.BordersOuter | ImGuiTableFlags.BordersInnerH
                | ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.NoSavedSettings)) return;
            try
            {
                var controlHeight = ImGui.GetFrameHeight();
                var headerHeight = controlHeight + ImGui.GetStyle().FramePadding.Y * 2;
                ImGui.TableNextRow(ImGuiTableRowFlags.None, headerHeight);
                ImGui.TableSetColumnIndex(0);
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(ImGuiCol.TableHeaderBg));
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (headerHeight - controlHeight) * 0.5f);
                if (ImGui.Selectable((expanded ? "- " : "+ ") + title + "###toggle", expanded,
                    ImGuiSelectableFlags.None, new Vector2(0, controlHeight)))
                { toggle(); expanded = !expanded; }
                if (expanded)
                {
                    ImGui.TableNextRow(); ImGui.TableSetColumnIndex(0);
                    ImGui.Indent(12);
                    try { drawSettings(); }
                    finally { ImGui.Unindent(12); }
                    ImGui.Spacing();
                }
            }
            finally { ImGui.EndTable(); }
        }
        finally { ImGui.PopID(); }
        ImGui.Spacing();
    }

    private void CancelSuperBindingEdit()
    {
        superRecording = -1; superBindingDraft = null; superBindingMessage = "";
        superEdges.BlockHeld(NativeHotbarBridge.IsKeyDown);
    }

    private void DrawSuperBarCards(Action drawSettings)
    {
        if (super is null) return;
        superSelectedBar = Math.Clamp(superSelectedBar, 0, 9);
        ImGui.TextDisabled("点击栏名展开设置，再次点击收起；一次展开一栏。");
        for (var index = 0; index < 10; index++)
        {
            var barIndex = index;
            var bar = super.Appearance[index];
            var logicalBar = NativeHotbarIdentity.LogicalBar(index, superPage);
            var title = $"第 {index + 1} 栏" + (index == 0 ? $"（内容页 {superPage + 1}）" : "") + $" · {bar.Columns}×{bar.Rows} · "
                + (super.Shared[logicalBar] ? "公用" : "职业专属") + (bar.Visible ? " · 显示" : " · 隐藏");
            var expanded = superSettingsExpanded && superSelectedBar == index;
            DrawHotbarSettingsCard("super-bar-" + index, title, expanded, () =>
            {
                CancelSuperBindingEdit();
                superSelectedBar = barIndex; superSettingsExpanded = !expanded;
            }, drawSettings);
        }
    }

    private void InheritSuperLayouts(int? onlyBar)
    {
        if (super is null) return;
        try
        {
            // Capture and validate the whole selection before touching the module archive.
            var layouts = SuperNativeArchive.CaptureLayouts(owner, onlyBar);
            foreach (var (index, layout) in layouts)
            {
                NativeHotbarLayout.ApplyNativeShape(super.Appearance[index], layout);
                super.Appearance[index].FollowNativeHud = false;
                reposition.Add(super.Appearance[index].Id);
            }
            targets.Clear(); localDragBar = null; localDragIndex = -1;
            superStatus = $"已继承 {layouts.Count} 条原生栏的格数样式；未重新读取内容与按键。未加载的原生栏保留模块原样式。";
            superDirty = true; SaveSuper(true);
        }
        catch (Exception error) { superStatus = "样式未继承：" + error.Message; }
    }

    private void DrawSuperAppearanceRepair()
    {
        if (super is null) return;
        if (super.AppearanceMappingVersion == 0)
            ImGui.TextWrapped("此档案需要更新布局。点击下方按钮重新读取位置、样式和缩放，保留内容与按键。");
        if (ImGui.Button("重新读取全部栏位布局"))
        {
            try
            {
                var native = SuperNativeArchive.CaptureAppearance(owner);
                foreach (var (index, appearance) in native)
                {
                    NativeHotbarLayout.ApplyNativeAppearance(super.Appearance[index], appearance);
                    super.Appearance[index].FollowNativeHud = true;
                    reposition.Add(super.Appearance[index].Id);
                }
                super.AppearanceMappingVersion = 1; super.NativeGeometryVersion = 1; superDirty = true;
                targets.Clear(); localDragBar = null; localDragIndex = -1;
                superStatus = "已更新全部栏位布局，内容与快捷键保持原样。";
                SaveSuper(true);
            }
            catch (Exception error) { superStatus = "外观未校正：" + error.Message; }
        }
    }

    private void DrawSuperNativeLayoutPicker(DragHotbar bar)
    {
        ImGui.TextUnformatted("格数样式（横向 × 纵向）");
        var scale = ImGui.GetIO().FontGlobalScale;
        var width = 64f * scale; var height = 76f * scale;
        var left = ImGui.GetCursorScreenPos().X;
        var right = left + ImGui.GetContentRegionAvail().X;
        for (var layout = 0; layout < NativeHotbarLayout.NativeColumns.Length; layout++)
        {
            NativeHotbarLayout.TryNativeShape(layout, out var columns, out var rows);
            if (layout != 0 && ImGui.GetItemRectMax().X + ImGui.GetStyle().ItemSpacing.X + width <= right) ImGui.SameLine();
            ImGui.PushID(layout);
            var selected = bar.Columns == columns && bar.Rows == rows;
            if (selected) ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonActive]);
            var min = ImGui.GetCursorScreenPos();
            var clicked = ImGui.Button("##native-layout", new(width, height));
            if (selected) ImGui.PopStyleColor();
            var list = ImGui.GetWindowDrawList();
            var cell = Math.Min(5f * scale, Math.Min((width - 14 * scale) / columns, 31f * scale / rows));
            var origin = min + new Vector2((width - cell * columns) / 2, (39 * scale - cell * rows) / 2 + 3 * scale);
            var color = ImGui.GetColorU32(selected ? ImGuiCol.CheckMark : ImGuiCol.TextDisabled);
            for (var slot = 0; slot < 12; slot++)
            {
                var start = origin + new Vector2(slot % columns, slot / columns) * cell;
                list.AddRectFilled(start, start + new Vector2(Math.Max(1, cell - scale)), color);
            }
            var label = $"{columns}×{rows}";
            list.AddText(min + new Vector2((width - ImGui.CalcTextSize(label).X) / 2, 43 * scale), ImGui.GetColorU32(ImGuiCol.Text), label);
            var center = min + new Vector2(width / 2, 65 * scale);
            list.AddCircle(center, 3 * scale, color);
            if (selected) list.AddCircleFilled(center, 2 * scale, color);
            if (clicked)
            {
                NativeHotbarLayout.ApplyNativeShape(bar, layout);
                bar.FollowNativeHud = false;
                superDirty = true; reposition.Add(bar.Id);
            }
            ImGui.PopID();
        }
    }

    private void DrawSuperAppearanceSettings()
    {
        if (!RequireSuperSettingsArchive()) return;
        DrawSuperAppearanceRepair();
        DrawSuperBarCards(DrawSuperAppearanceEditor);
    }

    private void DrawSuperAppearanceEditor()
    {
        if (!RequireSuperSettingsArchive()) return;
        {
            if (superSelectedBar == 0) ImGui.TextDisabled($"主栏当前显示第 {superPage + 1} 页。");
            var logicalBar = NativeHotbarIdentity.LogicalBar(superSelectedBar, superPage);
            var shared = super.Shared[logicalBar];
            if (ImGui.Checkbox("此栏为公用栏（所有职业同内容）", ref shared))
            {
                super.Shared[logicalBar] = shared; superDirty = true;
                if (superGroup >= 0)
                    for (var b = 0; b < 10; b++) super.Appearance[b].Slots = SuperHotbarRules.Resolve(super,
                        NativeHotbarIdentity.LogicalBar(b, superPage), superGroup, bridge!.DescribeSaved);
                CloseEditor(); replacing = null; received.Clear(); localDragBar = null;
            }
            ImGui.TextWrapped(shared ? "当前为公用栏：所有职业显示同一份内容。" : "当前为职业专属栏：切换职业时使用各自保存的内容。");
            var bar = super.Appearance[superSelectedBar];
            var hideEmpty = bar.HideEmpty;
            if (ImGui.Checkbox("隐藏本栏空格", ref hideEmpty)) { bar.HideEmpty = hideEmpty; superDirty = true; }
            ImGui.TextWrapped("隐藏空格不会移动其他格子；拖入内容时会临时显示空格。");
            var visible = bar.Visible;
            if (ImGui.Checkbox("显示此栏", ref visible)) { bar.Visible = visible; superDirty = true; }
            var follow = bar.FollowNativeHud;
            if (ImGui.Checkbox("跟随游戏栏位的位置、样式和缩放", ref follow))
            { bar.FollowNativeHud = follow; superDirty = true; }
            ImGui.TextWrapped("手动调整布局、位置或缩放后，会取消跟随。");
            DrawSuperNativeLayoutPicker(bar);
            if (ImGui.Button("读取本栏游戏样式")) InheritSuperLayouts(superSelectedBar);
            ImGui.SameLine();
            if (ImGui.Button("读取全部游戏样式")) InheritSuperLayouts(null);
            ImGui.TextWrapped("仅更新横竖排列，保留内容、按键和位置。");
            var scale = bar.Scale; if (ImGui.SliderFloat("栏位缩放", ref scale, 0.5f, 2.5f, "%.2f")) { bar.Scale = scale; bar.FollowNativeHud = false; superDirty = true; }
            var opacity = bar.Opacity; if (ImGui.SliderFloat("背景不透明度（0 为透明）", ref opacity, 0, 1, "%.2f")) { bar.Opacity = opacity; superDirty = true; }
            if (ImGui.Button("把此栏移到屏幕中央"))
            {
                var view = ImGui.GetMainViewport(); var size = NativeBarGeometry.FromSaved(bar).Size;
                var pos = NativeHotbarLayout.SetPosition(view.WorkPos + Vector2.Max(Vector2.Zero, view.WorkSize - size) / 2,
                    view.WorkPos, view.WorkSize, size, out var ax, out var ay);
                bar.X = pos.X - view.Pos.X; bar.Y = pos.Y - view.Pos.Y;
                bar.AnchorX = bar.AnchorY = null; bar.FollowNativeHud = false; superDirty = true;
            }
        }
    }

    private void DrawSuperBindingSettings()
    {
        if (!RequireSuperSettingsArchive()) return;
        DrawSuperBindingWarnings(super);
        ImGui.TextWrapped("展开栏位，选择格子，再录制主键或副键。录制时暂停模块热键，按 Esc 取消；设置只用于接管栏。");
        DrawSuperBarCards(DrawSuperBindingEditor);
    }

    private static void DrawSuperBindingWarnings(SuperHotbarDocument data)
    {
        var warnings = SuperHotbarRules.UnsupportedBindings(data).ToArray();
        if (warnings.Length == 0) return;
        ImGui.TextWrapped($"有 {warnings.Length} 个按键需要重新录制。原有绑定已保留，可通过“还原到游戏”恢复。");
        foreach (var warning in warnings) ImGui.TextWrapped(warning);
    }

    private void DrawSuperBindingEditor()
    {
        if (!RequireSuperSettingsArchive()) return;
        {
            if (ImGui.BeginCombo("选择格子", $"第 {superBindingSlot + 1} 格"))
            {
                for (var slot = 0; slot < 12; slot++)
                    if (ImGui.Selectable($"第 {slot + 1} 格", slot == superBindingSlot) && slot != superBindingSlot)
                    { superBindingSlot = slot; CancelSuperBindingEdit(); }
                ImGui.EndCombo();
            }
            var id = NativeHotbarIdentity.InputId(superSelectedBar, superBindingSlot);
            var binding = super.Bindings.Single(b => b.InputId == id);
            for (var alternate = 0; alternate < 2; alternate++)
            {
                ImGui.PushID(alternate);
                var key = alternate == 0 ? binding.Primary : binding.Secondary;
                ImGui.TextUnformatted((alternate == 0 ? "主键：" : "副键：") + key);
                if (ImGui.Button("录制组合键"))
                {
                    superRecording = alternate; superRecordingInput = id; superRecordArmed = false; superBindingDraft = null;
                    superEdges.BlockHeld(NativeHotbarBridge.IsKeyDown);
                }
                ImGui.SameLine(); if (ImGui.Button("清除"))
                {
                    if (alternate == 0) { binding.Primary = new(); binding.PrimaryEdited = true; }
                    else { binding.Secondary = new(); binding.SecondaryEdited = true; }
                    superDirty = true;
                }
                ImGui.PopID();
            }
            if (superRecording >= 0) ImGui.TextWrapped("请先松开按键，再按下新组合键；Esc 取消。需要游戏窗口在前台。");
            if (superBindingDraft is not null)
            {
                ImGui.TextWrapped("已录入：" + superBindingDraft);
                if (ImGui.Button("确认绑定"))
                {
                    var target = super.Bindings.Single(b => b.InputId == superRecordingInput);
                    var conflict = SuperHotbarRules.Conflict(super, target.InputId, superBindingDraft);
                    var other = SuperNativeArchive.OtherBindingConflict(superBindingDraft);
                    if (conflict >= 0) superBindingMessage = NativeHotbarIdentity.TryInput(conflict, out var conflictBar, out var conflictSlot)
                        ? $"这组按键已用于第 {conflictBar + 1} 栏第 {conflictSlot + 1} 格，请更换或先清除原绑定。"
                        : "这组按键已用于热键栏翻页，请更换或先清除原绑定。";
                    else if (other >= 0) superBindingMessage = "这组按键已用于游戏其他操作，请更换，或在游戏设置中解除原绑定。";
                    else
                    {
                        if (superRecording == 0) { target.Primary = superBindingDraft.Copy(); target.PrimaryEdited = true; }
                        else { target.Secondary = superBindingDraft.Copy(); target.SecondaryEdited = true; }
                        superDirty = true; superRecording = -1; superBindingDraft = null; superBindingMessage = "绑定已保存到模块。"; SaveSuper(true);
                    }
                }
            }
            if (superRecording >= 0) { ImGui.SameLine(); if (ImGui.Button("取消录制")) { superRecording = -1; superBindingDraft = null; } }
            ImGui.TextWrapped(superBindingMessage);
        }
    }
    private void RecordSuperKey()
    {
        if (!NativeHotbarBridge.IsGameForeground()) return;
        var down = NativeHotbarBridge.IsKeyDown;
        if (down(27)) { superRecording = -1; superBindingDraft = null; return; }
        if (superBindingDraft is not null) return;
        if (!superRecordArmed)
        { if (!Enumerable.Range(4, 251).Any(k => SuperInputKeys.Valid(k) && down(k))) superRecordArmed = true; return; }
        var modifiers = (down(16) ? 1 : 0) | (down(17) ? 2 : 0) | (down(18) ? 4 : 0);
        for (var key = 4; key <= 254; key++)
        {
            var shortcut = new SuperShortcut { Key = key, Modifiers = modifiers };
            if (SuperShortcut.Valid(shortcut) && down(key)) { superBindingDraft = shortcut; return; }
        }
    }

    private void DrawSuperRestoreSettings()
    {
        if (!RequireSuperSettingsArchive()) return;
        ImGui.TextWrapped("此页会把选中的模块配置写回游戏。只想使用游戏原有热键栏时，点击顶部“退出接管”即可。");
        ImGui.Separator();
        ImGui.TextUnformatted("1. 选择还原内容");
        var changed = ImGui.Checkbox("全部已备份职业的普通栏内容和公用属性", ref restoreContents);
        changed |= ImGui.Checkbox("热键栏相关主、副快捷键", ref restoreBindings);
        changed |= ImGui.Checkbox("热键栏显示、位置、缩放等设置", ref restoreOptions);
        changed |= ImGui.Checkbox("宏名称、图标和正文（覆盖个人宏与共通宏）", ref restoreMacros);
        changed |= ImGui.Checkbox("同时还原十字热键栏", ref restoreCross);
        if (changed) { restoreConfirm = false; superRestorePreview = null; }
        var selected = restoreContents || restoreBindings || restoreOptions || restoreMacros;
        var validAppearance = !restoreOptions || super.AppearanceMappingVersion == 1;
        ImGui.TextWrapped("横竖排列请在游戏界面布局中调整。不会还原其他界面或套装装备；十字栏需与栏内容或栏设置一同选择。");
        if (!validAppearance) ImGui.TextWrapped("请先退出接管，在“栏位与布局”重新读取全部布局，或取消还原栏设置。");
        ImGui.BeginDisabled(!selected || !validAppearance);
        if (ImGui.Button("2. 预览还原…"))
            try { BuildSuperRestorePreview(SuperNativeArchive.Capture(owner)); }
            catch (Exception error) { restoreConfirm = false; superRestorePreview = null; superRestoreResult = "无法生成还原预览，未写入游戏：" + error.Message; }
        ImGui.EndDisabled();
        if (restoreConfirm && selected && validAppearance)
        {
            DrawSuperRestorePreviewRows();
            ImGui.TextWrapped("确认后会先备份游戏当前配置，再还原并退出接管。完成后请重新登录，确认游戏已保存。");
            if (ImGui.Button("3. 确认写回游戏并退出接管")) ConfirmSuperRestore();
            ImGui.SameLine(); if (ImGui.Button("取消写回")) { restoreConfirm = false; superRestorePreview = null; }
        }
        if (superRestoreResult.Length > 0) { ImGui.Separator(); ImGui.TextWrapped(superRestoreResult); }
        if (!restoreConfirm && superRestoreBackupPath.Length > 0) ImGui.TextWrapped("还原前备份：" + superRestoreBackupPath);
    }
}
