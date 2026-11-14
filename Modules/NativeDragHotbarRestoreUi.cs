using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace OmniToolbox.NativeDragHotbarLocal;

public sealed partial class NativeDragHotbar
{
    private SuperRestorePreview? superRestorePreview;
    private WorkshopLiveHudCheck? superRestoreLiveCheck;
    private int superRestorePreviewPage;
    private (ulong Owner, long Started, int Changes, SuperRestoreScope Scope, bool Layout)? superRestoreSaveWatch;
    private SuperRestoreScope RestoreScope => new(restoreContents, restoreBindings, restoreOptions, restoreMacros, restoreCross);

    private void BuildSuperRestorePreview(SuperHotbarDocument current)
    {
        superRestoreLayoutTarget = superRestoreLayoutBefore = null;
        if (restoreHudLayout && HasSavedOtherLayout)
        {
            var target = SavedOtherLayout();
            WorkshopGameNative.Preflight(target, owner);
            superRestoreLayoutBefore = WorkshopGameNative.Capture(owner, target.Scope);
            superRestoreLayoutTarget = target;
        }
        superRestorePreview = SuperRestorePreview.Build(current, super!, RestoreScope, SuperNativeArchive.ExpectedBindingBytes);
        superRestorePreviewPage = 0; restoreConfirm = true;
        superRestoreResult = ""; superStatus = "已生成还原差异预览，尚未写入游戏。";
    }

    private void DrawRestoreSlotCell(SuperSavedSlot? slot, string fallback)
    {
        if (slot is null || slot.Type == 0 || bridge is not { Ready: true }) { ImGui.TextWrapped(fallback); return; }
        var entry = bridge.DescribeSaved(slot);
        var lookup = NativeHotbarItems.Icon(entry.IconId);
        if (entry.IconId > 0 && OmenTools.OmenService.ImageHelper.GetGameIcon(lookup.Id, lookup.Hq) is { } texture)
        {
            ImGui.Image(texture.Handle, new Vector2(24 * ImGui.GetIO().FontGlobalScale)); ImGui.SameLine();
        }
        ImGui.TextWrapped(entry.Name);
    }

    private void DrawSuperRestorePreviewRows()
    {
        if (superRestorePreview is not { } preview) return;
        if (superRestoreLayoutTarget is { } layout && superRestoreLayoutBefore is { } beforeLayout)
        {
            var differences = WorkshopGameRules.Compare(beforeLayout, layout);
            ImGui.TextWrapped($"其他界面布局：{differences.Count} 项差异。写入后切换至布局 {layout.CurrentLayout + 1} 并刷新界面，接管栏位置保留。");
        }
        if (!preview.Scope.Contents && !preview.Scope.Bindings && !preview.Scope.Options && !preview.Scope.Macros) return;
        ImGui.TextWrapped($"热键栏与宏：已核对 {preview.ComparedFields} 项，将变更 {preview.Changes} 项。");
        if (!preview.Scope.Options)
            ImGui.TextWrapped("未选择栏选项：不会修改原生栏的显示、位置或缩放；原来隐藏的原生栏仍会保持隐藏。");
        if (preview.UnsupportedCommands > 0)
            ImGui.TextWrapped($"注意：有 {preview.UnsupportedCommands} 个模块自定义指令不能直接写成原生格子，其对应位置将恢复为空格。请先核对内容差异。");
        if (preview.Changes == 0)
        {
            ImGui.TextWrapped("所选热键栏范围与游戏当前配置一致。确认后仍会提交保存并退出接管。");
            return;
        }
        const int pageSize = 20;
        var pages = (preview.Rows.Count + pageSize - 1) / pageSize;
        superRestorePreviewPage = Math.Clamp(superRestorePreviewPage, 0, pages - 1);
        if (pages > 1)
        {
            ImGui.BeginDisabled(superRestorePreviewPage == 0);
            if (ImGui.Button("上一页##restore-diff")) superRestorePreviewPage--;
            ImGui.EndDisabled(); ImGui.SameLine();
            ImGui.TextUnformatted($"{superRestorePreviewPage + 1} / {pages} 页"); ImGui.SameLine();
            ImGui.BeginDisabled(superRestorePreviewPage == pages - 1);
            if (ImGui.Button("下一页##restore-diff")) superRestorePreviewPage++;
            ImGui.EndDisabled();
        }
        var height = Math.Clamp(ImGui.GetContentRegionAvail().Y - 170 * ImGui.GetIO().FontGlobalScale, 110, 260);
        if (ImGui.BeginTable("##SuperRestoreDifferences", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg
            | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp, new Vector2(0, height)))
        {
            try
            {
                ImGui.TableSetupColumn("变更位置", ImGuiTableColumnFlags.None, 0.9f);
                ImGui.TableSetupColumn("游戏当前", ImGuiTableColumnFlags.None, 1.2f);
                ImGui.TableSetupColumn("将写入", ImGuiTableColumnFlags.None, 1.2f);
                ImGui.TableSetupScrollFreeze(0, 1); ImGui.TableHeadersRow();
                foreach (var row in preview.Rows.Skip(superRestorePreviewPage * pageSize).Take(pageSize))
                {
                    ImGui.TableNextRow(); ImGui.TableSetColumnIndex(0);
                    ImGui.TextWrapped(row.Category + " · " + row.Location);
                    ImGui.TableSetColumnIndex(1); DrawRestoreSlotCell(row.BeforeSlot, row.Before);
                    ImGui.TableSetColumnIndex(2); DrawRestoreSlotCell(row.AfterSlot, row.After);
                }
            }
            finally { ImGui.EndTable(); }
        }
    }

    private void ConfirmSuperRestore()
    {
        if (super is null || superRestorePreview is not { } preview) return;
        var scope = preview.Scope;
        var exited = false;
        try
        {
            var before = SuperNativeArchive.Capture(owner);
            WorkshopGameTemplate? layoutBefore = null;
            if (superRestoreLayoutTarget is { } layoutTarget)
            {
                WorkshopGameNative.Preflight(layoutTarget, owner);
                layoutBefore = WorkshopGameNative.Capture(owner, layoutTarget.Scope);
                if (superRestoreLayoutBefore is null
                    || WorkshopGameRules.Fingerprint(layoutBefore) != WorkshopGameRules.Fingerprint(superRestoreLayoutBefore)
                    || WorkshopGameRules.Fingerprint(SavedOtherLayout()) != WorkshopGameRules.Fingerprint(layoutTarget))
                {
                    BuildSuperRestorePreview(before);
                    superRestoreResult = "界面布局或模板已变化，预览已刷新，请重新核对后确认。"; return;
                }
            }
            var refreshed = SuperRestorePreview.Build(before, super, scope, SuperNativeArchive.ExpectedBindingBytes);
            if (!preview.SameAs(refreshed))
            {
                superRestorePreview = refreshed; superRestorePreviewPage = 0;
                superRestoreResult = "游戏配置或模块内容已变化，预览已刷新；本次没有写入，请重新核对后确认。";
                return;
            }
            superRestoreBackupPath = superRestoreLayoutBackupPath = ""; superRestoreSaveWatch = null; superRestoreLiveCheck = null;
            if (!scope.Contents && !scope.Bindings && !scope.Options && !scope.Macros
                && superRestoreLayoutTarget is { } onlyLayout && layoutBefore is not null)
            {
                ApplyWorkshopGameTemplate(onlyLayout, layoutBefore, false);
                superRestoreLayoutBackupPath = workshopGameBackupPath;
                restoreConfirm = false; superRestorePreview = null;
                superRestoreResult = ""; return;
            }
            var backupPath = SuperPath + ".before-restore-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + ".json";
            SuperHotbarStore.Save(backupPath, before, owner); superRestoreBackupPath = backupPath;
            if (layoutBefore is not null) superRestoreLayoutBackupPath = WorkshopGameBackupStore.Save(WorkshopDirectory, layoutBefore);
            var liveCheck = superRestoreLayoutTarget is { } layoutForCheck && layoutBefore is not null
                ? new WorkshopLiveHudCheck(layoutForCheck,layoutBefore,WorkshopGameNative.LiveHud(layoutForCheck)) : null;
            ExitSuperTakeover(); exited = true;
            if (superDirty) throw new InvalidOperationException("模块退出状态尚未保存成功，未写回游戏，请检查日志后重试。");
            if (scope.Contents || scope.Bindings || scope.Options || scope.Macros)
                SuperNativeArchive.Restore(super, owner, scope.Contents, scope.Bindings, scope.Options, scope.Macros, scope.Cross);
            if (superRestoreLayoutTarget is { } target && layoutBefore is not null)
                WorkshopGameNative.Apply(target, layoutBefore, owner);
            superRestoreLiveCheck = liveCheck;
            superFault = false;
            superRestoreResult = $"已应用 {preview.Changes} 项差异，原生内容及键位读回核对通过；已退出接管并向游戏提交保存。正在检查保存队列。";
            if (!scope.Options) superRestoreResult += " 未选择栏选项，原生栏显示、位置和缩放未改动。";
            if (layoutBefore is not null) superRestoreResult += " 已提交其他界面布局并刷新，请核对角色状态、Buff、小队列表和小地图的位置。";
            superRestoreSaveWatch = (owner, Environment.TickCount64, preview.Changes, scope, layoutBefore is not null);
            restoreConfirm = false; superRestorePreview = null;
        }
        catch (Exception error)
        {
            if (exited)
            {
                super.Enabled = false; superReady = false; superDirty = true; superFault = true; SaveSuper(true);
                superRestoreResult = "还原未完成，接管保持停用。写回不具备事务回滚能力，请核对原生设置。原因：" + error.Message;
            }
            else superRestoreResult = "还原预检未通过，未执行写回：" + error.Message;
            restoreConfirm = false; superRestorePreview = null;
        }
        finally
        {
            if (exited) { superGroup = -1; superEdges.BlockHeld(NativeHotbarBridge.IsKeyDown); }
            superStatus = superRestoreResult.Length > 0 ? superRestoreResult : workshopGameStatus;
        }
    }

    private void UpdateSuperRestoreSaveWatch()
    {
        if (superRestoreSaveWatch is not { } watch) return;
        if (owner != watch.Owner) { superRestoreSaveWatch = null; superRestoreLiveCheck = null; return; }
        if (Environment.TickCount64 - watch.Started < 250) return;
        if (!watch.Layout && !watch.Scope.Contents && !watch.Scope.Bindings && !watch.Scope.Macros)
        {
            superRestoreResult = $"已应用 {watch.Changes} 项差异，栏选项写回请求已提交并完成原生选项核对；请重新登录确认 HUD 设置持久保存。";
            superStatus = superRestoreResult; superRestoreSaveWatch = null; return;
        }
        try
        {
            if (SuperNativeArchive.RestoreSavePending(owner, watch.Scope)
                || watch.Layout && WorkshopGameNative.SavePending(owner, new(true, false, false, false)))
            {
                if (Environment.TickCount64 - watch.Started < 10_000) return;
                superRestoreResult = $"已应用 {watch.Changes} 项差异并通过原生读回核对；游戏保存仍在等待处理，尚不能确认保存完成。请检查游戏提示并稍后重新登录核对。";
            }
            else superRestoreResult = $"已应用 {watch.Changes} 项差异并通过原生读回核对；游戏保存队列已处理。已退出接管，请重新登录核对持久保存。";
            if (!watch.Scope.Options) superRestoreResult += " 原生栏显示、位置和缩放未改动（未选择栏选项）。";
            if (watch.Layout) superRestoreResult += " 其他界面布局已提交；请核对实际位置并在下次登录后确认。";
            if (superRestoreLiveCheck is { } live)
                superRestoreResult += " " + WorkshopLiveHudRules.Report(live,WorkshopGameNative.LiveHud(live.Target));
            superRestoreLiveCheck = null;
            superStatus = superRestoreResult; superRestoreSaveWatch = null;
        }
        catch (InvalidOperationException)
        {
            if (Environment.TickCount64 - watch.Started > 10_000)
            {
                superRestoreResult = "原生写回已提交，但角色或游戏模块暂不可读，未能完成保存队列检查；请重新登录核对。";
                superStatus = superRestoreResult; superRestoreSaveWatch = null;
            }
        }
    }
}
