using System.Reflection.PortableExecutable;
using System.Text.Json;
using OmniToolbox.NativeDragHotbarLocal;

internal static class ExecutionCompatibilityChecks
{
    internal static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        const long moduleBase = 0x140000000;
        const long searchBase = 0x200000000;
        const int textOffset = 4096;
        const int textSize = 34640770;
        const long wrapperAddress = moduleBase + 0x7D1CA0;
        const long executorAddress = moduleBase + 0x7D7870;
        check(NativeHotbarExecution.SnapshotAddress(wrapperAddress, moduleBase, searchBase,
            textOffset, textSize, 93) == searchBase + 0x7D1CA0,
            "a hooked wrapper maps by RVA into the original code copy");
        check(NativeHotbarExecution.SnapshotAddress(executorAddress, moduleBase, searchBase,
            textOffset, textSize, 25) == searchBase + 0x7D7870,
            "the executor independently maps to the code copy");
        check(NativeHotbarExecution.SnapshotAddress(wrapperAddress, moduleBase, moduleBase,
            textOffset, textSize, 93) == wrapperAddress,
            "non-copy scanners use the same validated range mapping");
        check(NativeHotbarExecution.SnapshotAddress(moduleBase + 4096, moduleBase, searchBase,
            4096, 100, 100) == searchBase + 4096,
            "a read ending exactly at the text-section boundary is valid");
        reject(() => NativeHotbarExecution.SnapshotAddress(moduleBase + 4095, moduleBase, searchBase,
            4096, 100, 1), "reads preceding the text section fail closed");
        reject(() => NativeHotbarExecution.SnapshotAddress(moduleBase + 4097, moduleBase, searchBase,
            4096, 100, 100), "reads crossing the text-section end fail closed");
        reject(() => NativeHotbarExecution.SnapshotAddress(moduleBase - 1, moduleBase, searchBase,
            4096, 100, 1), "addresses before the game module are rejected");
        reject(() => NativeHotbarExecution.SnapshotAddress(wrapperAddress, moduleBase, 0,
            textOffset, textSize, 93), "a null code-copy base is rejected");
        reject(() => NativeHotbarExecution.SnapshotAddress(wrapperAddress, moduleBase, searchBase,
            textOffset, textSize, 0), "zero-length native reads are rejected");
        reject(() => NativeHotbarExecution.SnapshotAddress(wrapperAddress, moduleBase, searchBase,
            textOffset, textSize, -1), "negative-length native reads are rejected");
        reject(() => NativeHotbarExecution.SnapshotAddress(moduleBase + 4096, moduleBase, searchBase,
            4096, 100, 101), "reads larger than the code section are rejected");
        reject(() => NativeHotbarExecution.SnapshotAddress(moduleBase + 4096, moduleBase, long.MaxValue,
            4096, 100, 1), "mapped pointer overflow is rejected");
        reject(() => NativeHotbarExecution.SnapshotAddress(moduleBase + 4096, moduleBase, long.MaxValue - 4096,
            4096, 100, 25), "the entire mapped read must fit without pointer overflow");

        var executor = Convert.FromHexString("48895C240848896C2420565741564883EC400FB682C7000000");
        NativeHotbarExecution.ValidateSlotExecutor(executor);
        check(true, "full native executor prologue and second-argument ABI are verified");
        reject(() => NativeHotbarExecution.ValidateSlotExecutor(executor.AsSpan(0, 24)),
            "a truncated executor fails ABI validation");
        executor[20] = 0x81;
        reject(() => NativeHotbarExecution.ValidateSlotExecutor(executor),
            "reading CommandType from the wrong register fails ABI validation");

        // Real hooked entrypoint bytes captured read-only on this CN client, 2026-11-08.
        var hookedWrapper = Convert.FromHexString("ff242550170040734583fa127519418bc04869d0e80000004881c2a00501004803d1e9a95b000073258bca498d91a000000048c1e104418bc04803c84869c1e8000000498bc94803d0e9825b0000488d91a0220100498bc9e9735b0000");
        var hookedExecutor = Convert.FromHexString("ff242500160040909090565741564883ec400fb682c7000000");
        reject(() => NativeHotbarExecution.Resolve(hookedWrapper, wrapperAddress),
            "captured live hook reproduces the 1.1.2 wrapper-validation failure");
        reject(() => NativeHotbarExecution.ValidateSlotExecutor(hookedExecutor),
            "captured executor hook must not be mistaken for original native instructions");

        var snapshotPath = Environment.GetEnvironmentVariable("NATIVE_DRAG_HOTBAR_CODE_SNAPSHOT");
        if (!string.IsNullOrWhiteSpace(snapshotPath))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(snapshotPath));
            var values = document.RootElement;
            var liveAddress = values.GetProperty("WrapperAddress").GetInt64();
            var target = values.GetProperty("ExecutorAddress").GetInt64();
            var originalWrapper = Convert.FromHexString(values.GetProperty("WrapperHex").GetString()!);
            var originalExecutor = Convert.FromHexString(values.GetProperty("ExecutorHex").GetString()!);
            check(NativeHotbarExecution.Resolve(originalWrapper, liveAddress) == target,
                "captured host snapshot resolves all branches using live addresses");
            NativeHotbarExecution.ValidateSlotExecutor(originalExecutor);
            check(true, "captured original executor passes full ABI validation");
            check(NativeHotbarExecution.SnapshotAddress(liveAddress,
                values.GetProperty("ModuleBase").GetInt64(), values.GetProperty("SearchBase").GetInt64(),
                textOffset, textSize, originalWrapper.Length)
                == values.GetProperty("SearchBase").GetInt64() + liveAddress - values.GetProperty("ModuleBase").GetInt64(),
                "captured runtime bases map to the correct host code-copy address");
        }

        var gamePath = Environment.GetEnvironmentVariable("NATIVE_DRAG_HOTBAR_GAME_EXE");
        if (!string.IsNullOrWhiteSpace(gamePath))
        {
            using var stream = File.OpenRead(gamePath);
            using var pe = new PEReader(stream);
            var wrapper = pe.GetSectionData(0x7D1CA0).GetContent(0, 93);
            var target = NativeHotbarExecution.Resolve(wrapper.AsSpan(), wrapperAddress);
            check(target == executorAddress, "installed CN executable resolves the expected pointer executor");
            NativeHotbarExecution.ValidateSlotExecutor(pe.GetSectionData(0x7D7870).GetContent(0, 25).AsSpan());
            check(true, "installed CN executable passes the executor ABI validation");
        }
    }
}
