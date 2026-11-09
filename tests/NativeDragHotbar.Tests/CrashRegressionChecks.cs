using System.Reflection.PortableExecutable;
using System.Text.Json;
using OmniToolbox.NativeDragHotbarLocal;

internal static class CrashRegressionChecks
{
    internal static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        const uint crashItem = 0xF540A;
        check(!NativeHotbarSafety.CanReadCombatState(2, crashItem, true),
            "crash HQ item 1004554 cannot enter the combat charge reader even if an action lookup succeeds");
        check(Enumerable.Range(0, 256).Where(t => t != 1)
            .All(t => !NativeHotbarSafety.CanReadCombatState((byte)t, 119, true)),
            "items macros crafting and every other non-action type bypass combat-only native state calls");
        check(NativeHotbarSafety.CanReadCombatState(1, 119, true)
            && !NativeHotbarSafety.CanReadCombatState(1, 119, false)
            && !NativeHotbarSafety.CanReadCombatState(1, 0, true),
            "combat state additionally requires a nonzero Action sheet identity");

        const long first = 0x100000;
        const int size = 0xE8;
        bool Slot(int bar, int index) => NativeHotbarSafety.IsOrdinarySlot(first + (bar * 16L + index) * size, first, size);
        check(Enumerable.Range(0, 10).All(b => Enumerable.Range(0, 12).All(s => Slot(b, s))),
            "the pointer executor intercepts all 120 ordinary slots");
        check(Enumerable.Range(0, 10).All(b => Enumerable.Range(12, 4).All(s => !Slot(b, s))),
            "reserved native positions are not intercepted as ordinary keyboard slots");
        check(!Slot(10, 0) && !Slot(17, 15) && !Slot(18, 0) && !Slot(20, 0)
            && !NativeHotbarSafety.IsOrdinarySlot(first - size, first, size)
            && !NativeHotbarSafety.IsOrdinarySlot(first + 1, first, size)
            && !NativeHotbarSafety.IsOrdinarySlot(0, first, size)
            && !NativeHotbarSafety.IsOrdinarySlot(first, first, 0),
            "cross pet external scratch null and misaligned pointers continue to native execution");
        check(Enumerable.Range(0, 10).All(b => !NativeHotbarSafety.IsSpecialMainBar((uint)b))
            && NativeHotbarSafety.IsSpecialMainBar(18),
            "normal main bar pages remain active while an actual pet replacement defers to native");

        var executor = Convert.FromHexString("48895C240848896C2420565741564883EC400FB682C7000000");
        NativeHotbarExecution.ValidateSlotExecutor(executor);
        check(true, "CN pointer executor prologue proves CommandType is read from second argument RDX");
        reject(() => NativeHotbarExecution.ValidateSlotExecutor(executor.AsSpan(0, 24)),
            "truncated slot executor cannot install a takeover hook");
        executor[20] = 0x81;
        reject(() => NativeHotbarExecution.ValidateSlotExecutor(executor),
            "a slot executor reading CommandType from another register fails ABI validation");

        check(NativeHotbarExecution.SnapshotAddress(0x7FF79FE00000 + 0x7D1CA0, 0x7FF79FE00000,
            0x1B615BA5040, 4096, 34640770, 93) == 0x1B615BA5040 + 0x7D1CA0,
            "hooked live wrapper maps to the host original-code snapshot at the same RVA");
        check(NativeHotbarExecution.SnapshotAddress(first + 4096, first, first, 4096, 100, 100) == first + 4096,
            "non-copy scanners remain supported when live code is original and the read fits exactly");
        reject(() => NativeHotbarExecution.SnapshotAddress(first + 4095, first, first, 4096, 100, 1),
            "snapshot reads before the text section are rejected");
        reject(() => NativeHotbarExecution.SnapshotAddress(first + 4097, first, first, 4096, 100, 100),
            "snapshot reads beyond the text section are rejected");
        reject(() => NativeHotbarExecution.SnapshotAddress(first + 4096, first, long.MaxValue, 4096, 100, 1),
            "snapshot address overflow is rejected before native memory is read");

        var snapshotPath = Environment.GetEnvironmentVariable("NATIVE_DRAG_HOTBAR_CODE_SNAPSHOT");
        if (!string.IsNullOrWhiteSpace(snapshotPath))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(snapshotPath));
            var values = document.RootElement;
            var address = values.GetProperty("WrapperAddress").GetInt64();
            var target = values.GetProperty("ExecutorAddress").GetInt64();
            var originalWrapper = Convert.FromHexString(values.GetProperty("WrapperHex").GetString()!);
            var originalExecutor = Convert.FromHexString(values.GetProperty("ExecutorHex").GetString()!);
            var patchedWrapper = Convert.FromHexString(values.GetProperty("PatchedWrapperHex").GetString()!);
            var patchedExecutor = Convert.FromHexString(values.GetProperty("PatchedExecutorHex").GetString()!);
            check(NativeHotbarExecution.Resolve(originalWrapper, address) == target,
                "MCP original snapshot resolves branches using live addresses despite existing entrypoint hooks");
            NativeHotbarExecution.ValidateSlotExecutor(originalExecutor);
            check(true, "MCP original snapshot passes the exact slot executor ABI check");
            reject(() => NativeHotbarExecution.Resolve(patchedWrapper, address),
                "read-only runtime evidence reproduces old wrapper validation failure on an existing hook");
            reject(() => NativeHotbarExecution.ValidateSlotExecutor(patchedExecutor),
                "read-only runtime evidence reproduces old executor validation failure on an existing hook");
        }

        var gamePath = Environment.GetEnvironmentVariable("NATIVE_DRAG_HOTBAR_GAME_EXE");
        if (!string.IsNullOrWhiteSpace(gamePath))
        {
            using var stream = File.OpenRead(gamePath);
            using var pe = new PEReader(stream);
            var wrapper = pe.GetSectionData(0x7D1CA0).GetContent(0, 93);
            var target = NativeHotbarExecution.Resolve(wrapper.AsSpan(), 0x7D1CA0);
            check(target == 0x7D7870, "installed CN executable resolves all indexed branches to the verified pointer executor");
            NativeHotbarExecution.ValidateSlotExecutor(pe.GetSectionData((int)target).GetContent(0, 25).AsSpan());
            check(true, "installed CN executable matches the actual two-argument slot executor hook ABI");
        }

        var profilePath = Environment.GetEnvironmentVariable("NATIVE_DRAG_HOTBAR_REPLAY_PROFILE");
        if (!string.IsNullOrWhiteSpace(profilePath))
        {
            // Read-only compatibility replay. Never save to the user's live configuration.
            var source = File.ReadAllText(profilePath);
            using var envelope = JsonDocument.Parse(source);
            using var payload = JsonDocument.Parse(envelope.RootElement.GetProperty("Payload").GetString()!);
            var owner = Convert.ToUInt64(payload.RootElement.GetProperty("Character").GetString(), 16);
            var data = SuperHotbarStore.Decode(source, owner);
            var roundtrip = SuperHotbarStore.Decode(SuperHotbarStore.Encode(data, owner), owner);
            check(JsonSerializer.Serialize(roundtrip) == JsonSerializer.Serialize(data),
                "existing 2.0.0 character archive and its edits survive read-only compatibility replay");
        }
    }
}
