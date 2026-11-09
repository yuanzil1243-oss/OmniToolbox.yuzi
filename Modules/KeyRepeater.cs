using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using FFXIVClientStructs.FFXIV.Client.UI;
using OmenTools;
using OmenTools.Interop.Windows.Helpers;
using OmniToolbox.Common.Module.Abstractions;
using OmniToolbox.Common.Module.Enums;
using OmniToolbox.Common.Module.Models;
using OmniToolbox.Config;
using OmniToolbox.Host;
using OmniToolbox.SuperHotbarInterop;
using OmniToolbox.UI;
using TreeTools.Lifecycle;
using GameFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

namespace OmniToolbox.KeyRepeaterOnline;

[ModuleApiVersion(1)]
public sealed unsafe class KeyRepeater : OmniModuleBase
{
    public const string Version = "0.2.7";
    private readonly KeyRepeatController repeat = new();
    private KeyRepeatKeyMap? keyMap;
    private KeyRepeatWorkshop? workshop;
    private Settings config = new();
    private string statusKey = "Disabled", errorKey = "";
    private long refreshAt;
    private int running, skipped;
    private bool faulted;

    public override ModuleInfo Info { get; } = new() { Author = "Yuzi", Category = ModuleCategory.Item };
    public override ModuleLocalization Localization { get; } = ModuleLocalization.Create(new Dictionary<string, string>
    {
        ["Title"] = "按键连点",
        ["Description"] = "自动读取技能栏主、副快捷键；支持原生“·”键及鼠标连点，并自动适配热键栏工坊的独立键盘快捷键。",
        ["Enabled"] = "启用技能栏按住连点", ["Cps"] = "每秒次数（CPS）",
        ["Version"] = "按键连点 {0}",
        ["Instructions"] = "无需逐键配置：未接管时使用游戏原生技能栏主、副快捷键，支持“·”、M3/M4/M5 及 Ctrl/Alt/Shift 组合，原生绑定约 1 秒更新。工坊接管时自动改用它导出的独立键盘快捷键；修饰键必须完全匹配，左右侧同义。",
        ["Summary"] = "间隔 {0:0.#} ms · 已识别 {1} 组 · 连点中 {2} 组 · {3}",
        ["Skipped"] = "另有 {0} 条不支持的键位未参与连点。",
        ["PauseHelp"] = "聊天或插件文字输入、游戏未在前台时暂停。切回游戏、修改设置后须松开再按。速度受游戏帧率限制，不补发积压输入。首次输入由真实按下触发。",
        ["WorkshopHelp"] = "工坊接管时，关闭工坊“内置连点”、打开“兼容按键连点模块”，再启用本模块。工坊内置连点开启或兼容开关关闭时，外部连点自动让出；接管期间不模拟原生栏（含宠物栏/任务动作），避免执行错格位。",
        ["WorkshopMouseHelp"] = "工坊 2.3.0 的外部连点接口不接受鼠标中键/侧键，本模块会跳过这些工坊绑定；需要时使用工坊内置连点。退出接管后，原生鼠标绑定仍可由本模块连点。",
        ["Mode"] = "绑定来源：{0}", ["NativeMode"] = "游戏原生技能栏", ["WorkshopMode"] = "热键栏工坊（外部接口）",
        ["WorkshopWaiting"] = "等待工坊接口就绪", ["WorkshopIdle"] = "工坊未导出外部连点键位（内置连点/兼容开关/未绑定）",
        ["InputHelp"] = "模拟输入可能不被部分应用支持，使用时请遵守对应服务条款。本模块不保存或上传实际键盘输入。",
        ["Reload"] = "重新读取技能栏绑定", ["Reset"] = "恢复默认（停用）",
        ["Disabled"] = "已停用", ["Faulted"] = "异常暂停", ["WaitingForLogin"] = "等待角色登录",
        ["BindingsUnavailable"] = "技能栏绑定不可用", ["NotForeground"] = "游戏未在前台",
        ["UiUnavailable"] = "等待角色界面就绪", ["TextInput"] = "文字输入中",
        ["Repeating"] = "正在连点", ["WaitingForKey"] = "等待按住技能栏快捷键",
        ["InputError"] = "读取或发送输入失败，已暂停；查看日志后可重新读取绑定。工坊接口异常时不会回退模拟原生按键。"
    });
    public override bool HasSettings => true;

    protected override void OnStart(FeatureLifetime activation)
    {
        if (!float.IsFinite(config.Cps) || config.Cps < 1 || config.Cps > 30) config.Cps = 10;
        repeat.Clear(); keyMap = null; running = skipped = 0;
        statusKey = "Disabled"; errorKey = ""; faulted = false; refreshAt = 0;
        var ipc = DalamudServices.PluginInterface;
        var active = ipc.GetIpcSubscriber<bool>(SuperHotbarRepeatProtocol.Active);
        var bindings = ipc.GetIpcSubscriber<int[]>(SuperHotbarRepeatProtocol.Bindings);
        var ticks = ipc.GetIpcSubscriber<int, bool, bool>(SuperHotbarRepeatProtocol.Repeat);
        workshop = new(() => (active.HasFunction ? 1 : 0) | (bindings.HasFunction ? 2 : 0) | (ticks.HasFunction ? 4 : 0),
            () => active.InvokeFunc(), () => bindings.InvokeFunc(), (chord, first) => ticks.InvokeFunc(chord, first));
        var framework = DalamudServices.Framework;
        activation.OnStopping(() => framework.Update -= OnUpdate);
        framework.Update += OnUpdate;
    }
    protected override void OnStop()
    {
        repeat.Clear(); keyMap = null; workshop = null; running = skipped = 0;
        statusKey = "Disabled"; errorKey = "";
    }
    public override bool DrawSettings()
    {
        var changed = ImGui.Checkbox(OmniLoc.Get(this, "Enabled"), ref config.Enabled);
        ImGui.TextDisabled(string.Format(OmniLoc.Get(this, "Version"), Version));
        var speedChanged = ImGui.SliderFloat(OmniLoc.Get(this, "Cps"), ref config.Cps, 1, 30, "%.1f");
        changed |= ImGui.IsItemDeactivatedAfterEdit();
        if (speedChanged) repeat.Suspend(Down);
        ImGui.TextWrapped(OmniLoc.Get(this, "Instructions"));
        ImGui.TextUnformatted(string.Format(OmniLoc.Get(this, "Summary"), 1000 / config.Cps,
            repeat.BindingCount, running, OmniLoc.Get(this, statusKey)));
        if (skipped > 0) ImGui.TextDisabled(string.Format(OmniLoc.Get(this, "Skipped"), skipped));
        ImGui.TextWrapped(OmniLoc.Get(this, "PauseHelp"));
        ImGui.TextWrapped(OmniLoc.Get(this, "WorkshopHelp"));
        ImGui.TextWrapped(OmniLoc.Get(this, "WorkshopMouseHelp"));
        ImGui.TextDisabled(string.Format(OmniLoc.Get(this, "Mode"), OmniLoc.Get(this,
            workshop?.Mode == KeyRepeatMode.Workshop ? "WorkshopMode" : workshop?.Mode == KeyRepeatMode.Native ? "NativeMode" : "WorkshopWaiting")));
        ImGui.TextWrapped(OmniLoc.Get(this, "InputHelp"));
        if (errorKey.Length > 0) ImGui.TextWrapped(OmniLoc.Get(this, errorKey));
        if (ImGui.Button(OmniLoc.Get(this, "Reload")))
        { faulted = false; refreshAt = 0; keyMap = null; workshop?.Reset(); repeat.Suspend(Down); errorKey = ""; }
        ImGui.SameLine();
        if (ImGui.Button(OmniLoc.Get(this, "Reset"))) changed |= ResetSettings();
        if (changed) { repeat.Suspend(Down); workshop?.Reset(); running = 0; faulted = false; errorKey = ""; refreshAt = 0; }
        return changed;
    }
    public override bool ResetSettings()
    {
        if (!config.Enabled && config.Cps == 10) return false;
        config = new(); repeat.Suspend(Down); workshop?.Reset(); refreshAt = 0; running = 0;
        return true;
    }
    private void OnUpdate(IFramework framework)
    {
        running = 0;
        if (faulted || !config.Enabled) { Pause(faulted ? "Faulted" : "Disabled"); return; }
        try
        {
            var ui = UIModule.Instance();
            if (ui == null || !DService.Instance().ClientState.IsLoggedIn) { Pause("WaitingForLogin"); return; }
            GetWindowThreadProcessId(GetForegroundWindow(), out var process);
            if (process != Environment.ProcessId) { Pause("NotForeground"); return; }
            var atk = RaptureAtkModule.Instance();
            if (atk == null || !atk->IsUiVisible) { Pause("UiUnavailable"); return; }
            if (atk->IsTextInputActive() || ImGui.GetIO().WantTextInput) { Pause("TextInput"); return; }
            if (Down(91) || Down(92)) { Pause("WaitingForKey"); return; }
            if (workshop == null) { Pause("WorkshopWaiting"); return; }
            if (workshop.Refresh()) { repeat.Suspend(Down); refreshAt = 0; }
            if (workshop.Mode == KeyRepeatMode.Unavailable) { Pause("WorkshopWaiting"); return; }
            if (workshop.Mode == KeyRepeatMode.Workshop)
            {
                repeat.ReplaceBindings(workshop.Bindings, Down);
                skipped = workshop.Skipped;
            }
            else if (Environment.TickCount64 >= refreshAt)
            {
                var input = ui->GetUIInputData();
                if (input == null) { Pause("BindingsUnavailable"); return; }
                keyMap ??= ReadKeyMap();
                var current = new HashSet<int>();
                skipped = 0;
                // Only ordinary hotbar slots, pet bar and duty actions; never movement or UI bindings.
                for (var id = (int)InputId.HOTBAR_1_1; id <= (int)InputId.HOTBAR_CONTENTS_ACT_R; id++)
                {
                    var bind = input->GetKeybind((InputId)id);
                    if (bind == null) continue;
                    foreach (var setting in bind->KeySettings)
                    {
                        if (keyMap.TryChord((int)setting.Key, (int)setting.KeyModifier, out var chord)) current.Add(chord);
                        else if (setting.Key != SeVirtualKey.NO_KEY) skipped++;
                    }
                }
                repeat.ReplaceBindings(current, Down);
                refreshAt = Environment.TickCount64 + 1000;
            }
            var modifiers = (Down(16) ? 1 : 0) | (Down(17) ? 2 : 0) | (Down(18) ? 4 : 0);
            running = repeat.Poll(Environment.TickCount64, config.Cps, modifiers, Down, chord => workshop.Send(chord, Send));
            statusKey = running > 0 ? "Repeating" : workshop.Mode == KeyRepeatMode.Workshop && repeat.BindingCount == 0 ? "WorkshopIdle" : "WaitingForKey";
        }
        catch (Exception ex)
        {
            faulted = true; Pause("Faulted"); errorKey = "InputError";
            DalamudServices.PluginLog.Error(ex, "按键连点运行失败，已暂停");
        }
    }
    private static KeyRepeatKeyMap ReadKeyMap()
    {
        var pairs = new List<(int Windows, int Native)>();
        for (var key = 8; key < 240; key++)
            if (DalamudServices.KeyState.TryGetSeVirtualKey(key, out var native)) pairs.Add((key, native));
        return new(pairs);
    }
    private static void Send(int key)
    {
        if (key is not (4 or 5 or 6))
        {
            // Physical modifiers stay held; never inject modifier releases.
            if (!KeyEmulationHelper.SendKeypress(key)) throw new InvalidOperationException("键盘输入发送失败");
            return;
        }
        var game = GameFramework.Instance();
        if (game == null || game->GameWindow == null || game->GameWindow->WindowHandle == 0)
            throw new InvalidOperationException("游戏输入窗口不可用");
        // Mouse bindings require mouse window messages, not WM_KEYDOWN/WM_KEYUP.
        // Both messages are synchronous; the module never owns a held mouse/modifier state.
        foreach (var message in KeyRepeatMouseInput.Messages(key, Down))
            SendMessage(game->GameWindow->WindowHandle, message.Id, message.WParam, 0);
    }
    private void Pause(string reason) { repeat.Suspend(Down); running = 0; statusKey = reason; }
    private static bool Down(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint SendMessage(nint window, uint message, nuint wParam, nint lParam);

    // Keep the API 1 Settings/config contract and old field names for host persistence.
    private sealed class Settings { public bool Enabled; public float Cps = 10; }
}
