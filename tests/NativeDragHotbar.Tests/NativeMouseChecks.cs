using System.Reflection;
using System.Runtime.InteropServices;

internal static class NativeMouseChecks
{
    internal static void Compiled(Assembly assembly, Assembly structs, Action<bool, string> check)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var type = assembly.GetType("OmniToolbox.NativeDragHotbarLocal.NativeHotbarMouseSuppression", true)!;
        Type Native(string name) => structs.GetType("FFXIVClientStructs.FFXIV.Component.GUI." + name, true)!;
        var unit = Native("AtkUnitBase"); var node = Native("AtkResNode"); var stageType = Native("AtkStage");
        var tooltipType = Native("AtkTooltipManager"); var collisionType = Native("AtkCollisionManager");
        int Offset(Type t, string field) => t.GetField(field)!.GetCustomAttribute<FieldOffsetAttribute>()!.Value;
        var allocated = new List<nint>();
        nint Allocate(int size) { var p = Marshal.AllocHGlobal(size); Marshal.Copy(new byte[size], 0, p, size); allocated.Add(p); return p; }
        nint New(Type t) => Allocate(t.StructLayoutAttribute!.Size);
        void Pointer(nint p, Type t, string field, nint value) => Marshal.WriteIntPtr(p, Offset(t, field), value);
        void Number(nint p, Type t, string field, int value) => Marshal.WriteInt32(p, Offset(t, field), value);
        int Read(nint p, Type t, string field) => Marshal.ReadInt32(p, Offset(t, field));
        nint ReadPointer(nint p, Type t, string field) => Marshal.ReadIntPtr(p, Offset(t, field));
        try
        {
            var a = New(unit); var b = New(unit); var root = New(node); var child = New(node); var anchor = New(node);
            var list = Allocate(128); var list2 = Allocate(128); var stage = New(stageType); var collision = New(collisionType);
            var tooltip = stage + Offset(stageType, "TooltipManager"); var current = a; var hides = 0;
            void Setup(nint addon, int count, nint listPtr)
            {
                Marshal.WriteInt16(addon, Offset(unit, "Id"), 33);
                Pointer(addon, unit, "RootNode", root); Pointer(addon, unit, "CollisionNodeList", listPtr);
                Number(addon, unit, "CollisionNodeListCount", count);
            }
            Setup(a, 12, list); Setup(b, 7, list2);
            Pointer(child, node, "ParentNode", root);
            Pointer(stage, stageType, "AtkCollisionManager", collision);
            Pointer(collision, collisionType, "IntersectingAddon", a);
            Pointer(collision, collisionType, "IntersectingCollisionNode", child);
            Pointer(tooltip, tooltipType, "TargetNode", child);
            Marshal.WriteInt16(tooltip, Offset(tooltipType, "ParentAddonId"), 33);
            Marshal.WriteInt16(root, Offset(node, "NodeFlags"), 0x35);
            var lease = type.GetConstructors(flags).Single(c => c.GetParameters().Length == 3).Invoke([
                (Func<int, nint>)(i => i == 0 ? current : 0), (Func<nint>)(() => stage),
                (Action<ushort>)(id => { if (id != 33) throw new Exception("wrong tooltip owner"); hides++; Pointer(tooltip, tooltipType, "TargetNode", 0); })]);
            void Call(string method, params object[] args) => type.GetMethod(method, flags)!.Invoke(lease, args);
            Call("Suppress", 0);
            check(Read(a, unit, "CollisionNodeListCount") == 0 && ReadPointer(collision, collisionType, "IntersectingAddon") == 0
                && ReadPointer(collision, collisionType, "IntersectingCollisionNode") == 0 && hides == 1,
                "actual mouse suppression empties collision count, clears owned native hover and hides only the descendant tooltip");
            check(Marshal.ReadInt16(root, Offset(node, "NodeFlags")) == 0x35 && ReadPointer(a, unit, "CollisionNodeList") == list,
                "mouse suppression preserves root visibility bits and collision-list allocation");
            Call("Suppress", 0); Call("Release", 0); Call("Release", 0);
            check(Read(a, unit, "CollisionNodeListCount") == 12, "repeat suppression retains original count and repeated release is idempotent");
            Pointer(tooltip, tooltipType, "TargetNode", anchor); Call("Suppress", 0);
            check(hides == 1 && ReadPointer(tooltip, tooltipType, "TargetNode") == anchor,
                "detached workshop-owned tooltip anchor survives native hover cleanup");
            Call("Release", 0);
            Pointer(tooltip, tooltipType, "TargetNode", child); Marshal.WriteInt16(tooltip, Offset(tooltipType, "ParentAddonId"), 44);
            Call("Suppress", 0); check(hides == 1, "another addon's tooltip is never hidden by bar suppression"); Call("Release", 0);
            Call("Suppress", 0); Number(a, unit, "CollisionNodeListCount", 9); Call("Release", 0);
            check(Read(a, unit, "CollisionNodeListCount") == 9, "release never overwrites a fresh nonzero count supplied by the game");
            Call("Suppress", 0); Pointer(a, unit, "CollisionNodeList", list2); Number(a, unit, "CollisionNodeListCount", 8);
            Call("Suppress", 0); Call("Release", 0);
            check(Read(a, unit, "CollisionNodeListCount") == 8, "regenerated collision list acquires a fresh lease and restores its own count");
            Call("Suppress", 0); current = b; Call("Release", 0);
            check(Read(b, unit, "CollisionNodeListCount") == 7, "addon replacement cannot restore stale count into a new addon");
            current = a; Number(a, unit, "CollisionNodeListCount", 11); Call("Suppress", 0);
            Marshal.WriteInt16(a, Offset(unit, "Id"), 55); Call("Release", 0);
            check(Read(a, unit, "CollisionNodeListCount") == 0, "changed addon identity invalidates the cached lease without dereferencing old nodes");
            Number(a, unit, "CollisionNodeListCount", 5000);
            try { Call("Suppress", 0); throw new Exception("invalid count accepted"); }
            catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException)
            { check(Read(a, unit, "CollisionNodeListCount") == 5000, "unrecognized collision count fails without native mutation"); }
            Number(a, unit, "CollisionNodeListCount", 6); Call("Suppress", 0); Call("ReleaseAll");
            check(Read(a, unit, "CollisionNodeListCount") == 6, "lifecycle release restores the live native collision count");
        }
        finally { foreach (var p in allocated) Marshal.FreeHGlobal(p); }
        var module = assembly.GetType("OmniToolbox.NativeDragHotbarLocal.NativeDragHotbar", true)!;
        string[] Calls(string name) => ItemAndFontChecks.Calls(module.GetMethod(name, flags)!).ToArray();
        check(Calls("OnSuperNativeMouse").Contains("Suppress") && Calls("OnSuperNativeMouse").Contains("Release")
            && Calls("UpdateSuper").Contains("Suppress") && Calls("UpdateSuperHudContext").Contains("RestoreSuperVisibility"),
            "compiled framework, addon update/draw/finalize and HUD-editing paths manage native mouse interception");
    }
}
