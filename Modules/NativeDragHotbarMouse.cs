using System;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace OmniToolbox.NativeDragHotbarLocal;

// A transient lease on the collision count, never on visibility or saved HUD state.
// Resolve the addon again before restoring: game transitions can destroy/rebuild it.
internal sealed unsafe class NativeHotbarMouseSuppression
{
    private readonly record struct Lease(nint Addon, ushort Id, nint Root, nint List, uint Count);
    private readonly Lease?[] leases = new Lease?[10];
    private readonly Func<int, nint> resolve;
    private readonly Func<nint> stageAddress;
    private readonly Action<ushort> hideTooltip;

    internal NativeHotbarMouseSuppression() : this(i => (nint)SuperNativeArchive.Addon(i),
        () => (nint)AtkStage.Instance(), id =>
        { var stage = AtkStage.Instance(); if (stage != null) stage->TooltipManager.HideTooltip(id, true); }) { }

    internal NativeHotbarMouseSuppression(Func<int, nint> resolve, Func<nint> stage, Action<ushort> hide)
    { this.resolve = resolve; stageAddress = stage; hideTooltip = hide; }

    internal void Suppress(int index)
    {
        if (index is < 0 or >= 10) throw new ArgumentOutOfRangeException(nameof(index));
        var addon = (AtkUnitBase*)resolve(index);
        if (addon == null || addon->RootNode == null || addon->Id == 0) { leases[index] = null; return; }
        if (leases[index] is { } previous && !Matches(previous, addon)) leases[index] = null;
        var count = addon->CollisionNodeListCount;
        if (count > 4096 || count > 0 && addon->CollisionNodeList == null)
            throw new InvalidOperationException("原生栏鼠标节点不可识别，已停止接管。");
        if (count > 0)
        {
            // The game may rebuild the collision list during Update; adopt the fresh list.
            leases[index] = new((nint)addon, addon->Id, (nint)addon->RootNode, (nint)addon->CollisionNodeList, count);
            addon->CollisionNodeListCount = 0;
        }
        ClearHover(addon);
    }

    private static bool Matches(Lease lease, AtkUnitBase* addon) => (nint)addon == lease.Addon
        && addon->Id == lease.Id && (nint)addon->RootNode == lease.Root && (nint)addon->CollisionNodeList == lease.List;

    private void ClearHover(AtkUnitBase* addon)
    {
        var stage = (AtkStage*)stageAddress(); if (stage == null) return;
        var collisions = stage->AtkCollisionManager;
        if (collisions != null && collisions->IntersectingAddon == addon)
        { collisions->IntersectingAddon = null; collisions->IntersectingCollisionNode = null; }
        var tooltip = &stage->TooltipManager;
        if (tooltip->ParentAddonId != addon->Id) return;
        // The workshop's own tooltip uses a detached anchor with no native parent chain.
        var target = tooltip->TargetNode;
        for (var depth = 0; target != null && depth < 128; depth++, target = target->ParentNode)
            if (target == addon->RootNode) { hideTooltip(addon->Id); break; }
    }

    internal void Release(int index)
    {
        var lease = leases[index]; leases[index] = null;
        if (lease is not { } saved) return;
        var addon = (AtkUnitBase*)resolve(index);
        if (addon != null && Matches(saved, addon) && addon->CollisionNodeListCount == 0)
            addon->CollisionNodeListCount = saved.Count;
    }

    internal void ReleaseAll()
    {
        // Release every lease even if one addon is temporarily unavailable or mismatched.
        Exception? failure = null;
        for (var index = 0; index < 10; index++)
            try { Release(index); } catch (Exception error) { failure ??= error; }
        if (failure is not null) throw failure;
    }
}
