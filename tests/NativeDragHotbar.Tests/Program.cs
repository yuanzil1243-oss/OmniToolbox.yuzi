using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using OmniToolbox.NativeDragHotbarLocal;

var checks = 0;
void Check(bool valid, string name)
{ if (!valid) throw new Exception(name); Console.WriteLine("PASS " + name); checks++; }
void Reject(Action action, string name)
{
    try { action(); }
    catch (InvalidDataException) { Check(true, name); return; }
    throw new Exception("Expected rejection: " + name);
}
var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
var temp = Path.Combine(Path.GetTempPath(), "NativeDragHotbarTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
const ulong owner = 0x1122334455667788;
var path = Path.Combine(temp, "profile.json");
try
{
    FeatureChecks.Run(root, temp, Check, Reject);
    SettingsBehaviorChecks.Run(Check);
    SharedProfilesChecks.Run(temp, Check, Reject);
    ClipboardBackupChecks.Run(temp, Check, Reject);
    WorkshopProfileChecks.Run(temp, Check, Reject);
    SuperHotbarChecks.Run(temp, Check, Reject);
    CrashRegressionChecks.Run(Check, Reject);
    ExecutionCompatibilityChecks.Run(Check, Reject);
    NativeLayoutChecks.Run(Check, Reject);
    MappingAndRepeatChecks.Run(root, Check, Reject);
    KeyboardAndVisualChecks.Run(root, Check, Reject);
    ItemAndFontChecks.Run(root, Check, Reject);
    NativeTooltipChecks.Run(Check);
    MacroDetailChecks.Run(Check);
    RestorePreviewChecks.Run(Check);
    NativeReadChecks.Run(root, temp, Check, Reject);
    GeometryAndChargeChecks.Run(root, Check, Reject);
    var profile = HotbarProfileStore.Load(path, owner, out var recovered);
    Check(!recovered && profile.Owner == owner.ToString("X16") && profile.Bars.Single().Slots.Count == 12, "new character gets twelve empty slots");
    profile.Bars.Add(new() { Name = "第二栏 面板" });
    Check(HotbarRules.TryResolveBarSelector(profile.Bars, "#1", out var barIndex) && barIndex == 0
        && HotbarRules.TryResolveBarSelector(profile.Bars, "2", out barIndex) && barIndex == 1
        && HotbarRules.TryResolveBarSelector(profile.Bars, "第二栏 面板", out barIndex) && barIndex == 1
        && HotbarRules.TryResolveBarSelector(profile.Bars, "#2 第二栏 面板", out barIndex) && barIndex == 1
        && !HotbarRules.TryResolveBarSelector(profile.Bars, "不存在", out _), "bar commands resolve ordinal and full custom names");
    profile.Bars.RemoveAt(1);
    Check(HotbarPresentation.TryIconDirective("/micon 惊讶 emote\n/surprised motion\n/em 发现<t>！", out var directive)
        && directive == new MacroIconDirective("惊讶", "emote"), "the user's micon emote example selects the native emote identity");
    Check(HotbarPresentation.TryIconDirective("/macroicon \"Attack Name\" action", out directive)
        && directive == new MacroIconDirective("Attack Name", "action"), "quoted macro icon names can contain spaces");
    Check(HotbarPresentation.TryIconDirective("／MICON\t“惊讶”\tEMOTE", out directive)
        && directive == new MacroIconDirective("惊讶", "emote"), "macro icons accept fullwidth slash, smart quotes, tabs and case variants");
    Check(HotbarPresentation.TryIconDirective("/micon 技能名称", out directive) && directive.Category == "action",
        "omitted macro icon category defaults to action");
    Check(HotbarPresentation.TryIconDirective("/宏图标 惊讶 emote", out directive) && directive.Name == "惊讶",
        "localized macro icon spelling is recognized");
    Check(!HotbarPresentation.TryIconDirective("/echo /micon 惊讶 emote", out _)
        && !HotbarPresentation.TryIconDirective("/micon", out _)
        && !HotbarPresentation.TryIconDirective("/micon \"unfinished", out _),
        "ordinary messages and incomplete macro icon directives cannot override icons");
    Check(HotbarPresentation.TryIconDirective("/micon 惊讶 emote\n/micon 其他 emote", out directive) && directive.Name == "惊讶",
        "the first macro icon directive determines the display");
    var iconEntry = new HotbarEntry { Mode = HotbarEntryMode.Command, Command = "/micon 惊讶 emote", IconId = 123, IconOverride = true };
    Check(HotbarPresentation.SelectIcon(iconEntry, 246201, 999) == 246201 && iconEntry.IconId == 123 && iconEntry.IconOverride,
        "a resolved command icon outranks manual and native icons without overwriting the manual choice");
    Check(HotbarPresentation.SelectIcon(iconEntry, 0, 999) == 123,
        "removing or failing to resolve micon restores the manual icon");
    iconEntry.IconOverride = false;
    Check(HotbarPresentation.SelectIcon(iconEntry, 0, 999) == 999,
        "native automatic icons follow command and manual icon priorities");
    iconEntry.Mode = HotbarEntryMode.Native;
    Check(HotbarPresentation.SelectIcon(iconEntry, 246201, 999) == 999,
        "inactive custom commands cannot replace an active native-mode icon");
    var fallback = new HotbarEntry { Mode = HotbarEntryMode.Command, Command = "/echo test" };
    var randomCalls = 0;
    Check(HotbarPresentation.EnsureFallbackIcon(fallback, 0, (min, max) =>
        { randomCalls++; if (min != 234001 || max != 234064) throw new Exception("Wrong fallback range"); return max - 1; })
        && fallback.IconId == 234063 && !fallback.IconOverride,
        "iconless command entries receive a stored automatic icon from the requested inclusive range");
    Check(!HotbarPresentation.EnsureFallbackIcon(fallback, 0, (min, max) => { randomCalls++; return min; }) && randomCalls == 1,
        "the random automatic icon is assigned once instead of changing during redraw or save");
    var emptyIcon = new HotbarEntry { Mode = HotbarEntryMode.Command, Command = "  " };
    Check(!HotbarPresentation.EnsureFallbackIcon(emptyIcon, 0, (min, max) => min) && emptyIcon.IconId == 0,
        "empty slots remain empty and never receive random icons");
    var resolvedIcon = new HotbarEntry { Mode = HotbarEntryMode.Command, Command = "/micon 惊讶 emote" };
    Check(!HotbarPresentation.EnsureFallbackIcon(resolvedIcon, 246201, (min, max) => min),
        "a valid command icon does not require an unnecessary fallback assignment");
    Check(HotbarRules.Commands("/micon 惊讶 emote\n/surprised motion\n/em 发现<t>！")
        .Where(line => !HotbarPresentation.IsIconLine(line)).SequenceEqual(new[] { "/surprised motion", "/em 发现<t>！" }),
        "icon metadata is consumed locally while the remaining macro commands retain order");
    Check(HotbarPresentation.WithOpacity(0xCC123456, 0) == 0x00123456
        && HotbarPresentation.WithOpacity(0xCC123456, 1) == 0xCC123456
        && HotbarPresentation.WithOpacity(0xCC123456, 0.5f) == 0x66123456,
        "opacity scales existing alpha to zero while preserving RGB and the original tint");
    var transparentTints = HotbarPresentation.SlotTints(0, 1, 0xFF555555);
    Check(transparentTints.Surface == 0x00161616 && transparentTints.Frame == 0x00555555,
        "zero bar opacity hides the slot surface and frame");
    Check(transparentTints.Image == 0xFFFFFFFF,
        "icons remain fully visible at zero bar opacity");
    var halfTints = HotbarPresentation.SlotTints(0.5f, 1, 0xFF555555);
    Check(halfTints.Image == transparentTints.Image
        && halfTints.Surface == 0x66161616 && halfTints.Frame == 0x80555555,
        "changing bar opacity only changes the slot surface and border");
    var themeTints = HotbarPresentation.SlotTints(0.5f, 0.5f, 0xFF555555);
    Check(themeTints.Image == 0x80FFFFFF && themeTints.Surface == 0x33161616,
        "icon tint still respects the host theme alpha independently of bar opacity");
    Check(HotbarPresentation.EditorShade(false) == 0 && HotbarPresentation.EditorShade(true) == 0x2E000000,
        "the editor receives a light black dim layer only while replacement is pending");
    Check(HotbarPresentation.ReplacementCenter(true, new(100, 200), new(500, 800), new(0, 0), new(1920, 1080)) == new System.Numerics.Vector2(300, 500)
        && HotbarPresentation.ReplacementCenter(false, new(100, 200), new(500, 800), new(100, 50), new(1000, 700)) == new System.Numerics.Vector2(600, 400),
        "replacement confirmation follows the editor center with a viewport-center fallback");
    var visibility = new HotbarProfile { Owner = owner.ToString("X16") };
    visibility.Bars.Add(new() { Name = "第二栏 面板" });
    Check(HotbarVisibilityCommands.TryParse("show", out var visibilityAction, out var selector)
        && visibilityAction == HotbarVisibilityAction.Toggle && selector == ""
        && HotbarVisibilityCommands.Apply(visibility, visibilityAction, selector, out _) && !visibility.Show,
        "the previously advertised show command now hides an already visible overlay");
    Check(HotbarVisibilityCommands.Apply(visibility, visibilityAction, selector, out _) && visibility.Show,
        "repeating the show alias restores the overlay");
    Check(HotbarVisibilityCommands.TryParse("TOGGLE\t#2", out visibilityAction, out selector)
        && HotbarVisibilityCommands.Apply(visibility, visibilityAction, selector, out var selectedBar) && selectedBar == 1 && !visibility.Bars[1].Visible,
        "standalone and routed visibility arguments support tabs and ordinal selectors");
    Check(HotbarVisibilityCommands.TryParse("toggle \"第二栏 面板\"", out visibilityAction, out selector)
        && HotbarVisibilityCommands.Apply(visibility, visibilityAction, selector, out selectedBar) && selectedBar == 1 && visibility.Bars[1].Visible,
        "quoted full names resolve and toggle the intended bar");
    visibility.Show = false;
    Check(HotbarVisibilityCommands.Apply(visibility, HotbarVisibilityAction.Toggle, "#2", out _) && visibility.Show && visibility.Bars[1].Visible,
        "toggling a bar hidden by the global switch makes it actually visible");
    visibility.Bars.ForEach(bar => bar.Visible = false);
    Check(HotbarVisibilityCommands.Apply(visibility, HotbarVisibilityAction.Toggle, "", out _) && visibility.Show && visibility.Bars.All(bar => bar.Visible),
        "global toggle can recover when every individual bar was hidden");
    Check(!HotbarVisibilityCommands.TryParse("invalid #1", out _, out _)
        && !HotbarVisibilityCommands.Apply(visibility, HotbarVisibilityAction.Hide, "unknown", out _) && visibility.Show,
        "invalid visibility commands leave existing state untouched");
    Check(HotbarVisibilityCommands.TryParse("on #1", out visibilityAction, out selector) && visibilityAction == HotbarVisibilityAction.Show
        && HotbarVisibilityCommands.Apply(true, visibilityAction)
        && HotbarVisibilityCommands.TryParse("off", out visibilityAction, out _) && !HotbarVisibilityCommands.Apply(true, visibilityAction),
        "on and hide/off retain explicit show and hide behavior");
    var transparent = new HotbarProfile { Owner = owner.ToString("X16") };
    transparent.Bars[0].Opacity = 0;
    transparent.Bars[0].Slots[0] = fallback.Copy();
    var transparentPath = Path.Combine(temp, "transparent.json");
    HotbarProfileStore.Save(transparentPath, transparent, owner, false);
    var transparentReload = HotbarProfileStore.Load(transparentPath, owner, out _);
    var transparentShare = HotbarTransfer.Parse(HotbarTransfer.Export(transparentReload, null, true, "1.1.0"));
    Check(transparentShare.Bars[0].Opacity == 0 && transparentShare.Bars[0].Slots[0].IconId == 234063
        && !transparentShare.Bars[0].Slots[0].IconOverride,
        "zero opacity and a stable automatic icon survive save/reload and settings transfer");
    transparent.Bars[0].Opacity = -0.01f;
    Reject(() => HotbarRules.Validate(transparent, owner), "negative opacity remains invalid");
    transparent.Bars[0].Opacity = 1.01f;
    Reject(() => HotbarRules.Validate(transparent, owner), "opacity above one remains invalid");
    var migrated = NativeHotbarLayout.ResolvePosition(300, 200, null, null, new(100, 50), new(1000, 700), new(200, 100), out var ax, out var ay);
    Check(migrated == new System.Numerics.Vector2(300, 200) && Math.Abs(ax - 0.25f) < 0.001f && Math.Abs(ay - 0.25f) < 0.001f,
        "legacy absolute positions migrate to viewport-relative anchors");
    var resized = NativeHotbarLayout.ResolvePosition(300, 200, ax, ay, new(100, 50), new(1200, 800), new(200, 100), out _, out _);
    Check(resized == new System.Numerics.Vector2(350, 225), "anchored positions follow game viewport resizing");
    var draggedPosition = NativeHotbarLayout.SetPosition(new(1500, -100), new(100, 50), new(1000, 700), new(200, 100), out ax, out ay);
    Check(draggedPosition == new System.Numerics.Vector2(900, 50) && ax == 1 && ay == 0,
        "dragged positions clamp to the usable viewport and refresh their anchors");

    Check(HotbarRules.IsValidLayout(1, 1) && HotbarRules.IsValidLayout(7, 3)
        && HotbarRules.IsValidLayout(10, 12) && HotbarRules.IsValidLayout(120, 1),
        "custom layouts accept arbitrary positive dimensions up to 120 slots");
    Check(!HotbarRules.IsValidLayout(0, 3) && !HotbarRules.IsValidLayout(3, -1)
        && !HotbarRules.IsValidLayout(11, 11) && !HotbarRules.IsValidLayout(int.MaxValue, int.MaxValue),
        "invalid and overflowing layout dimensions are rejected");
    var layoutProfile = new HotbarProfile { Owner = owner.ToString("X16") };
    var layoutBar = layoutProfile.Bars[0];
    layoutBar.Name = "可变栏"; layoutBar.Scale = 1.25f; layoutBar.Opacity = 0.65f;
    layoutBar.AnchorX = 0.75f; layoutBar.AnchorY = 0.4f;
    layoutBar.Slots[0] = new() { NativeType = 1, NativeId = 119, Name = "第一格" };
    HotbarRules.Resize(layoutBar, 6, 4);
    layoutBar.Slots[23] = new() { Mode = HotbarEntryMode.Command, Command = "/echo 隐藏格", Name = "末格" };
    Check(layoutBar.SlotCount == 24 && layoutBar.Slots.Count == 24 && layoutBar.Slots[0].NativeId == 119,
        "expanding a layout adds empty slots and preserves the existing native action");
    var retainedTail = layoutBar.Slots[23];
    HotbarRules.Resize(layoutBar, 2, 2);
    Check(layoutBar.SlotCount == 4 && layoutBar.Slots.Count == 24 && ReferenceEquals(retainedTail, layoutBar.Slots[23]),
        "shrinking only hides slots instead of deleting their contents");
    Check(layoutBar.Scale == 1.25f && layoutBar.Opacity == 0.65f && layoutBar.AnchorX == 0.75f,
        "resizing retains appearance and position anchors");
    var layoutPath = Path.Combine(temp, "layout-profile.json");
    HotbarProfileStore.Save(layoutPath, layoutProfile, owner, false);
    var layoutReload = HotbarProfileStore.Load(layoutPath, owner, out _);
    Check(layoutReload.Bars[0].Rows == 2 && layoutReload.Bars[0].Columns == 2
        && layoutReload.Bars[0].Slots[23].Command == "/echo 隐藏格", "hidden slots survive save and reload");
    HotbarRules.Resize(layoutReload.Bars[0], 8, 3);
    Check(layoutReload.Bars[0].SlotCount == 24 && layoutReload.Bars[0].Slots[23].Name == "末格",
        "increasing the visible capacity restores previously hidden contents in order");
    var beforeResize = JsonSerializer.Serialize(layoutBar);
    Reject(() => HotbarRules.Resize(layoutBar, 121, 1), "oversized layout cannot be applied");
    Check(JsonSerializer.Serialize(layoutBar) == beforeResize, "rejected resize leaves every existing slot and setting unchanged");
    var shortProfile = new HotbarProfile { Owner = owner.ToString("X16") };
    shortProfile.Bars[0].Rows = 2;
    Reject(() => HotbarRules.Validate(shortProfile, owner), "profile validation rejects insufficient slots for a layout");
    shortProfile.Bars[0].Rows = 1;
    while (shortProfile.Bars[0].Slots.Count <= HotbarRules.MaxSlots) shortProfile.Bars[0].Slots.Add(new());
    Reject(() => HotbarRules.Validate(shortProfile, owner), "stored slots cannot exceed the per-bar limit");

    foreach (var legacyColumns in new[] { 12, 6, 4, 3, 2, 1 })
    {
        var legacy = new HotbarProfile { SchemaVersion = 1, Owner = owner.ToString("X16") };
        legacy.Bars[0].Columns = legacyColumns;
        legacy.Bars[0].Name = "旧栏"; legacy.Bars[0].X = 123; legacy.Bars[0].Y = 234;
        legacy.Bars[0].Slots[11] = new() { NativeType = 1, NativeId = 119, Name = "旧版末格" };
        var legacyJson = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(legacy))!;
        legacyJson["Bars"]![0]!.AsObject().Remove("Rows");
        var legacyPath = Path.Combine(temp, $"legacy-{legacyColumns}.json");
        File.WriteAllText(legacyPath, legacyJson.ToJsonString());
        var upgraded = HotbarProfileStore.Load(legacyPath, owner, out var legacyRecovered);
        Check(!legacyRecovered && upgraded.SchemaVersion == 2 && upgraded.NeedsUpgradeSave
            && upgraded.Bars[0].Rows == 12 / legacyColumns && upgraded.Bars[0].SlotCount == 12
            && upgraded.Bars[0].Slots[11].Name == "旧版末格" && upgraded.Bars[0].X == 123,
            $"legacy {legacyColumns} x {12 / legacyColumns} layout migrates without content or position loss");
        HotbarProfileStore.Save(legacyPath, upgraded, owner, false);
        Check(!upgraded.NeedsUpgradeSave
            && JsonDocument.Parse(File.ReadAllText(legacyPath)).RootElement.GetProperty("SchemaVersion").GetInt32() == 2
            && JsonDocument.Parse(File.ReadAllText(legacyPath + ".bak")).RootElement.GetProperty("SchemaVersion").GetInt32() == 1,
            $"migration {legacyColumns} persists the new schema while backing up the original profile");
    }

    var shareText = HotbarTransfer.Export(layoutProfile, 0, true, "1.1.0");
    var shared = HotbarTransfer.Parse(shareText);
    Check(!shareText.Contains("\"Owner\"") && shared.Bars.Count == 1
        && !shared.ShowAllBars.HasValue && !shared.ShowCommandMessages.HasValue && !shared.ShowHelp.HasValue,
        "single-bar export contains no character ownership or global toggle settings");
    Check(shared.Bars[0].Columns == 2 && shared.Bars[0].Rows == 2 && shared.Bars[0].Slots.Count == 24
        && shared.Bars[0].Slots[23].Command == "/echo 隐藏格" && shared.Bars[0].AnchorX == 0.75f,
        "bar export round-trips its active layout, hidden contents and position anchors");
    layoutProfile.Show = false;
    var allShared = HotbarTransfer.Parse(HotbarTransfer.Export(layoutProfile, null, false, "1.1.0"));
    Check(allShared.ShowAllBars == false && allShared.ShowCommandMessages == false && !allShared.ShowHelp.HasValue,
        "all-settings export includes visibility and command-feedback preferences without the removed help toggle");
    var legacyShared = HotbarTransfer.Parse(HotbarTransfer.Export(layoutProfile, null, false, "2.2.5")
        .Replace("ShowCommandMessages", "ShowHelp"));
    Check(legacyShared.ShowHelp == false && legacyShared.ShowCommandMessages is null,
        "legacy extension backup parses without applying old help setting to command feedback");
    var destinationProfile = new HotbarProfile { Owner = (owner + 1).ToString("X16") };
    destinationProfile.Bars[0].Name = "可变栏";
    var firstImported = HotbarTransfer.Append(destinationProfile, owner + 1, shared);
    Check(firstImported == 1 && destinationProfile.Bars.Count == 2
        && destinationProfile.Owner == (owner + 1).ToString("X16") && destinationProfile.Show,
        "cross-character import appends bars without replacing ownership or global settings");
    Check(destinationProfile.Bars[1].Id != shared.Bars[0].Id && destinationProfile.Bars[1].Name == "可变栏 (导入2)"
        && !ReferenceEquals(destinationProfile.Bars[1].Slots[23], shared.Bars[0].Slots[23]),
        "import regenerates bar identities, resolves duplicate names and deep-copies hidden slots");
    HotbarTransfer.Append(destinationProfile, owner + 1, shared);
    Check(destinationProfile.Bars[2].Name == "可变栏 (导入3)"
        && destinationProfile.Bars.Select(bar => bar.Id).Distinct().Count() == 3,
        "repeated import creates unique numbered names and identities");
    var importSnapshot = JsonSerializer.Serialize(destinationProfile);
    shared.Bars[0].Slots[0].NativeType = 255;
    Reject(() => HotbarTransfer.Append(destinationProfile, owner + 1, shared), "invalid native content fails import validation");
    Check(JsonSerializer.Serialize(destinationProfile) == importSnapshot,
        "failed import leaves every destination bar and setting intact");
    shared = HotbarTransfer.Parse(shareText);
    var fullProfile = new HotbarProfile { Owner = owner.ToString("X16"),
        Bars = Enumerable.Range(0, HotbarRules.MaxBars).Select(_ => new DragHotbar()).ToList() };
    var fullSnapshot = JsonSerializer.Serialize(fullProfile);
    Reject(() => HotbarTransfer.Append(fullProfile, owner, shared), "import cannot exceed the twelve-bar limit");
    Check(JsonSerializer.Serialize(fullProfile) == fullSnapshot, "capacity rejection never partially appends imported bars");
    Reject(() => HotbarTransfer.Parse("{not-json"), "malformed clipboard data is rejected");
    Reject(() => HotbarTransfer.Parse(shareText.Replace("NativeDragHotbar.Settings", "DifferentModule")),
        "another module's export cannot be mistaken for hotbar settings");
    var futureShare = System.Text.Json.Nodes.JsonNode.Parse(shareText)!;
    futureShare["Version"] = 99;
    Reject(() => HotbarTransfer.Parse(futureShare.ToJsonString()), "unsupported transfer format versions are rejected");
    var brokenShare = System.Text.Json.Nodes.JsonNode.Parse(shareText)!;
    brokenShare["Bars"]![0]!["Rows"] = 0;
    Reject(() => HotbarTransfer.Parse(brokenShare.ToJsonString()), "imported layouts must obey positive dimensions");
    brokenShare = System.Text.Json.Nodes.JsonNode.Parse(shareText)!;
    brokenShare["Bars"]![0]!["Slots"]![0]!["Command"] = new string('x', 2049);
    Reject(() => HotbarTransfer.Parse(brokenShare.ToJsonString()), "oversized imported commands are rejected before preview");
    var importedPath = Path.Combine(temp, "imported-profile.json");
    HotbarProfileStore.Save(importedPath, destinationProfile, owner + 1, false);
    Check(HotbarProfileStore.Load(importedPath, owner + 1, out _).Bars[1].Slots[23].Command == "/echo 隐藏格",
        "imported hidden commands persist under the destination character");
    var multiBarExport = HotbarTransfer.Parse(HotbarTransfer.Export(destinationProfile, null, true, "1.1.0"));
    Check(multiBarExport.Bars.Count == 3 && multiBarExport.Bars[2].Slots[23].Command == "/echo 隐藏格",
        "all-settings export round-trips multiple bars and their hidden entries");
    HotbarRules.Resize(layoutReload.Bars[0], 10, 12);
    layoutReload.Bars[0].Slots[119] = new() { NativeType = 2, NativeId = 1004551, Name = "第120格 HQ物品" };
    HotbarRules.Resize(layoutReload.Bars[0], 1, 1);
    var maximumShare = HotbarTransfer.Parse(HotbarTransfer.Export(layoutReload, 0, true, "1.1.0"));
    Check(maximumShare.Bars[0].SlotCount == 1 && maximumShare.Bars[0].Slots.Count == 120
        && maximumShare.Bars[0].Slots[119].NativeId == 1004551,
        "the 120th slot and its HQ identity survive shrinking to one slot and transfer");
    Reject(() => HotbarTransfer.Parse(""), "empty clipboard cannot replace any hotbar settings");

    profile.Bars[0].Slots[0] = new() { NativeType = 1, NativeId = 119, IconId = 405, Name = "技能", Tooltip = "说明" };
    profile.Bars[0].Slots[1] = new() { Mode = HotbarEntryMode.Command, Command = "/echo 测试\n/echo two", IconId = 1 };
    HotbarProfileStore.Save(path, profile, owner, false);
    var loaded = HotbarProfileStore.Load(path, owner, out recovered);
    Check(!recovered && loaded.Bars[0].Slots[0].NativeId == 119 && loaded.Bars[0].Slots[1].Command.Contains('\n'), "native identity and multiline commands survive save/reload");
    loaded.Bars[0].Slots[0].Mode = HotbarEntryMode.Command;
    loaded.Bars[0].Slots[0].Command = "/echo extra";
    HotbarProfileStore.Save(path, loaded, owner, false);
    var combined = HotbarProfileStore.Load(path, owner, out recovered).Bars[0].Slots[0];
    Check(!recovered && combined.Mode == HotbarEntryMode.Command && combined.HasNative && combined.NativeType == 1
        && combined.NativeId == 119 && HotbarRules.Commands(combined.Command).SequenceEqual(new[] { "/echo extra" }),
        "command mode preserves the native action and its additional commands across save/reload");
    var combinedDraft = combined.Copy(); combinedDraft.Command += "\n/echo second";
    Check(combinedDraft.HasNative && combinedDraft.NativeId == combined.NativeId
        && HotbarRules.Commands(combinedDraft.Command).SequenceEqual(new[] { "/echo extra", "/echo second" }),
        "editing the command list retains the native action for first execution");
    loaded = HotbarProfileStore.Load(path, owner, out recovered);
    loaded.Bars[0].Slots[0].Mode = HotbarEntryMode.Native;
    loaded.Bars[0].Slots[0].Command = "";
    var copy = loaded.Bars[0].Slots[0].Copy(); copy.Mode = HotbarEntryMode.Command; copy.Command = "/echo override";
    Check(copy.HasNative && loaded.Bars[0].Slots[0].Mode == HotbarEntryMode.Native && loaded.Bars[0].Slots[0].Command == "", "editing a draft preserves the source and native restore identity");
    loaded.Bars[0].Name = "修改后的栏";
    HotbarProfileStore.Save(path, loaded, owner, false);
    Check(File.Exists(path + ".bak") && !Directory.EnumerateFiles(temp, "*.tmp-*").Any(), "atomic save leaves a backup and no temporary files");
    File.WriteAllText(path, "{broken");
    var backupBytes = File.ReadAllBytes(path + ".bak");
    loaded = HotbarProfileStore.Load(path, owner, out recovered);
    Check(recovered && loaded.Bars[0].Slots[0].NativeId == 119 && Directory.EnumerateFiles(temp, "*.corrupt-*").Count() == 1, "corrupt main file restores backup and preserves original");
    HotbarProfileStore.Save(path, loaded, owner, recovering: true);
    Check(File.ReadAllBytes(path + ".bak").SequenceEqual(backupBytes), "recovery save does not replace good backup with corrupt data");
    Reject(() => HotbarProfileStore.Load(path, owner + 1, out _), "another character cannot load this profile");
    var snapshot = File.ReadAllBytes(path);
    loaded.Owner = "different-owner";
    Reject(() => HotbarProfileStore.Save(path, loaded, owner, false), "wrong owner cannot overwrite profile");
    Check(File.ReadAllBytes(path).SequenceEqual(snapshot), "rejected save preserves existing bytes");
    loaded.Owner = owner.ToString("X16"); loaded.SchemaVersion = 99;
    Reject(() => HotbarRules.Validate(loaded, owner), "future schema rejected");
    File.WriteAllText(path, JsonSerializer.Serialize(loaded));
    Reject(() => HotbarProfileStore.Load(path, owner, out _), "future main schema cannot silently roll back to an older backup");
    loaded.SchemaVersion = 2; loaded.Bars[0].Scale = float.NaN;
    Reject(() => HotbarRules.Validate(loaded, owner), "nonfinite layout rejected");
    loaded.Bars[0].Scale = 1; loaded.Bars[0].Slots[0].NativeType = 255;
    Reject(() => HotbarRules.Validate(loaded, owner), "unknown native action type rejected");
    loaded.Bars[0].Slots[0].NativeType = 1; loaded.Bars[0].Slots[0].NativeId = 0;
    Reject(() => HotbarRules.Validate(loaded, owner), "zero skill ID rejected");
    loaded.Bars[0].Slots[0].NativeId = 119; loaded.Bars[0].Slots[1].Command = new('a', 2049);
    Reject(() => HotbarRules.Validate(loaded, owner), "oversized command rejected");
    loaded.Bars[0].Slots[1].Command = ""; loaded.Bars.Add(loaded.Bars[0]);
    Reject(() => HotbarRules.Validate(loaded, owner), "duplicate bar identities rejected");
    Check(HotbarRules.IsValidId(7, 0) && HotbarRules.IsValidId(7, 0x100) && !HotbarRules.IsValidId(7, 0x200)
        && !HotbarRules.IsValidId(7, 100), "personal/shared macro indices validated including index zero");
    Check(HotbarRules.IsValidId(15, 0) && HotbarRules.IsValidId(18, 255) && HotbarRules.IsValidId(8, 17), "zero gearset, extended target markers and clear-all field marker are retained");
    Check(!HotbarRules.IsSupportedType(3) && !HotbarRules.IsSupportedType(5) && !HotbarRules.IsSupportedType(14), "unstable inventory-location types cannot be persisted");
    Check(HotbarRules.Commands("\n echo 一; \r\n／echo 二\n\\plugin test\n;\n").SequenceEqual(new[] { "/echo 一", "/echo 二", "\\plugin test" }), "commands normalize fullwidth slash, CRLF, whitespace and empty lines");
    var macroAction = new HotbarEntry { NativeType = 1, NativeId = 119, Name = "热狙击弹" };
    Check(HotbarRules.MacroDropText(macroAction) == "热狙击弹"
        && HotbarRules.MacroDropText(new() { NativeType = 12, NativeId = 3, Name = "跨服通讯贝" }) == "跨服通讯贝",
        "native macro text drops insert action and main-command names as shown in the user's video");
    var emote = new HotbarEntry { NativeType = 6, NativeId = 1, Name = "疑问" };
    Check(HotbarRules.MacroDropText(emote, "/consider") == "/consider"
        && HotbarRules.MacroDropText(emote, "／consider") == "/consider"
        && HotbarRules.MacroDropText(emote) is null,
        "emote drops use the supplied game TextCommand instead of guessing from the display name");
    Check(HotbarRules.TryAppendMacroCommand("/echo before", "/consider", out var macroText)
        && macroText == "/echo before\n/consider", "macro drops preserve existing text and append a new line");
    Check(HotbarRules.TryAppendMacroCommand("/echo before\n", "热狙击弹", out macroText)
        && macroText == "/echo before\n热狙击弹"
        && HotbarRules.TryAppendMacroCommand("", "/consider", out macroText) && macroText == "/consider",
        "macro drops avoid redundant blank lines at the end and work in an empty editor");
    Check(!HotbarRules.TryAppendMacroCommand(new('x', 2048), "/consider", out macroText)
        && macroText == new string('x', 2048), "oversized macro insertion preserves the complete prior text");
    var editorDraft = macroAction.Copy(); editorDraft.Mode = HotbarEntryMode.Command;
    Check(HotbarRules.TryAppendMacroCommand(editorDraft.Command, "/consider", out macroText), "macro text can be appended to an existing native-action draft");
    editorDraft.Command = macroText;
    Check(editorDraft.NativeType == macroAction.NativeType && editorDraft.NativeId == macroAction.NativeId
        && macroAction.Command == "", "macro text insertion retains the native identity and leaves the unsaved source untouched");
    File.WriteAllText(path, "bad"); File.WriteAllText(path + ".bak", "also bad");
    Reject(() => HotbarProfileStore.Load(path, owner, out _), "two broken files fail closed");
    Check(File.ReadAllText(path) == "bad" && File.ReadAllText(path + ".bak") == "also bad", "load failure never replaces either source");

    var wrapper = Convert.FromHexString("4c8bc94183f810734583fa127519418bc04869d0e80000004881c2a00501004803d1e9a95b000073258bca498d91a000000048c1e104418bc04803c84869c1e8000000498bc94803d0e9825b0000488d91a0220100498bc9e9735b0000");
    Check(NativeHotbarExecution.Resolve(wrapper, 0x1407D1CA0) == 0x1407D7870, "actual CN indexed-wrapper fixture resolves to its pointer-based executor");
    wrapper[74] ^= 1;
    Reject(() => NativeHotbarExecution.Resolve(wrapper, 0x1407D1CA0), "disagreeing executor branches fail closed");
    wrapper[74] ^= 1; wrapper[0] = 0;
    Reject(() => NativeHotbarExecution.Resolve(wrapper, 0x1407D1CA0), "changed client machine-code shape fails closed");

    Check(NativeHotbarPayload.TryDecode(1, 0, 119, out var nativeId) && nativeId == 119,
        "skill payload uses Int2 when the source context is zero");
    Check(NativeHotbarPayload.TryDecode(10, 999, 23, out nativeId) && nativeId == 23,
        "general action payload uses Int2 rather than source context");
    Check(NativeHotbarPayload.TryDecode(7, 12345, 0, out nativeId) && nativeId == 0,
        "macro zero remains a valid payload ID");
    Check(!NativeHotbarPayload.TryDecode(1, 119, 0, out _) && !NativeHotbarPayload.TryDecode(1, 119, -1, out _)
        && !NativeHotbarPayload.TryDecode(255, 0, 119, out _), "invalid payloads cannot fall back to a plausible Int1");

    var inventoryReceiver = Convert.FromHexString("458b4c2404488d542430458b0424488d4c2434e81747b8fe8b7424340fbf442430c1e6100bf0");
    Check(NativeHotbarInventory.Resolve(inventoryReceiver, 0x141582AB1) == 0x1401071E0,
        "actual CN action-bar inventory receiver resolves its UI-position converter");
    inventoryReceiver[19] = 0x90;
    Reject(() => NativeHotbarInventory.Resolve(inventoryReceiver, 0x141582AB1), "changed inventory call opcode fails closed");
    inventoryReceiver[19] = 0xE8; inventoryReceiver[33] ^= 1;
    Reject(() => NativeHotbarInventory.Resolve(inventoryReceiver, 0x141582AB1), "changed inventory packing instruction fails closed");
    Check(NativeHotbarInventory.TryPackPosition(0, 2, out var packedPosition) && packedPosition == 2,
        "native inventory converter outputs container and slot, not item ID and type");
    Check(NativeHotbarInventory.TryPackPosition(1, 34, out packedPosition) && packedPosition == 0x10022,
        "sorted inventory location packs exactly as the native action-bar receiver");
    Check(!NativeHotbarInventory.TryPackPosition(99999, 2, out _) && !NativeHotbarInventory.TryPackPosition(0, -1, out _),
        "invalid container and negative slot fail closed");
    Check(NativeHotbarInventory.MatchesItem(4551, 2, 4551) && NativeHotbarInventory.MatchesItem(1004551, 2, 1004551)
        && !NativeHotbarInventory.MatchesItem(1004551, 2, 4551) && !NativeHotbarInventory.MatchesItem(4551, 1, 4551)
        && !NativeHotbarInventory.MatchesItem(4551, 2, 34), "normalized inventory identity must equal the actual NQ/HQ item in that slot");
    Check(NativeHotbarInventory.IsNormalized(6, 1, 6, 1) && NativeHotbarInventory.IsNormalized(12, 3, 12, 3)
        && NativeHotbarInventory.IsNormalized(7, 0x100, 7, 0x100) && NativeHotbarInventory.IsNormalized(15, 0, 15, 0),
        "emote, main-command, shared macro and gearset identities retain their established handling");
    Check(!NativeHotbarInventory.IsNormalized(6, 1, 6, 2) && !NativeHotbarInventory.IsNormalized(1, 119, 6, 119),
        "non-inventory identities cannot silently change during normalization");
    var inventoryProfile = new HotbarProfile { Owner = owner.ToString("X16") };
    inventoryProfile.Bars[0].Slots[0] = new() { NativeType = 2, NativeId = 1004551, Name = "HQ item" };
    var inventoryPath = Path.Combine(temp, "inventory-profile.json");
    HotbarProfileStore.Save(inventoryPath, inventoryProfile, owner, false);
    Check(HotbarProfileStore.Load(inventoryPath, owner, out _).Bars[0].Slots[0].NativeId == 1004551,
        "inventory-derived HQ item survives persistence as an item ID rather than a bag position");

    var session = new NativeHotbarDragSession();
    var dragged = new HotbarEntry { NativeType = 1, NativeId = 119, Name = "技能" };
    var deliveries = 0;
    HotbarEntry? delivered = null;
    bool Receive(HotbarEntry entry) { deliveries++; delivered = entry; return true; }
    session.Begin(1);
    session.Observe(true, false, true, dragged);
    Check(!session.Release(true, false, true, true, Receive) && deliveries == 0, "held drag cannot activate a slot");
    dragged.NativeId = 120;
    Check(session.Release(false, false, true, true, Receive) && deliveries == 1 && delivered!.NativeId == 119,
        "frame polling delivers a copied snapshot without a native Drop callback");
    Check(!session.Release(false, false, true, true, Receive) && deliveries == 1,
        "subsequent native callback cannot duplicate a polled release");
    session.Begin(1); session.Observe(true, false, true, dragged);
    Check(!session.Release(false, false, true, false, Receive) && !session.Release(false, false, true, true, Receive),
        "release outside the bar cannot drop later merely by moving into it");
    session.Observe(true, false, true, dragged);
    Check(session.Release(false, false, true, true, Receive) && deliveries == 2,
        "click-to-drop mode can rearm on a subsequent real press and release");
    foreach (var reason in new[] { "Escape or right-click", "foreground lost" })
    {
        session.Begin(1); session.Observe(true, false, true, dragged);
        session.Observe(true, reason == "Escape or right-click", reason != "foreground lost", dragged);
        Check(!session.Release(false, false, true, true, Receive), reason + " blocks the rest of this drag session");
    }
    session.Begin(1); session.Observe(false, false, true, dragged);
    Check(!session.Release(false, false, true, true, Receive), "a drag first seen with no held left button is not a synthetic drop");
    session.Begin(1); session.Observe(true, false, true, null);
    Check(!session.Release(false, false, true, true, Receive), "unsupported source cannot reuse a previous drag snapshot");
    session.Begin(1); session.Observe(true, false, true, dragged); session.End();
    Check(!session.Active && !session.Release(false, false, true, true, Receive), "native cancellation clears the pending release");
    session.Begin(1); session.Observe(true, false, true, dragged);
    Check(!session.Release(false, false, true, true, _ => false)
        && !session.Release(false, false, true, true, Receive), "rejected target cannot retry the same release");
    session.Begin(1); session.Observe(true, false, true, dragged);
    var nestedDelivery = false;
    Check(session.Release(false, false, true, true, _ =>
    { nestedDelivery = session.Release(false, false, true, true, Receive); return true; }) && !nestedDelivery,
        "reentrant callbacks cannot deliver the same session twice");

    var omni = Environment.GetEnvironmentVariable("NATIVE_DRAG_HOTBAR_OMNI_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncherCN/installedPlugins/OmniToolbox/1.1.8.9");
    var dalamud = Environment.GetEnvironmentVariable("NATIVE_DRAG_HOTBAR_DALAMUD_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncherCN/addon/Hooks/dev");
    var context = new AssemblyLoadContext("hotbar-contract", true);
    context.Resolving += (_, name) =>
    {
        foreach (var dir in new[] { omni, dalamud })
        { var candidate = Path.Combine(dir, name.Name + ".dll"); if (File.Exists(candidate)) return context.LoadFromAssemblyPath(candidate); }
        return null;
    };
    var dll = Path.Combine(root, "build/NativeDragHotbar/bin/Release/net10.0-windows7.0/NativeDragHotbar.dll");
    var assembly = context.LoadFromAssemblyPath(dll);
    var type = assembly.GetType("OmniToolbox.NativeDragHotbarLocal.NativeDragHotbar", true)!;
    var host = context.LoadFromAssemblyPath(Path.Combine(omni, "OmniToolbox.Common.dll"));
    host.GetType("OmniToolbox.Common.Module.Runtime.ModuleContract", true)!
        .GetMethod("ValidateType", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [type]);
    Check(true, $"compiled module passes actual Omni {Path.GetFileName(omni)} API 1 contract validator");
    var module = Activator.CreateInstance(type)!;
    var structs = context.LoadFromAssemblyPath(Path.Combine(dalamud, "FFXIVClientStructs.dll"));
    var actionBar = structs.GetType("FFXIVClientStructs.FFXIV.Client.UI.AddonActionBarX", true)!;
    MappingAndRepeatChecks.CheckNativeInputs(structs, Check);
    Check(System.Runtime.InteropServices.Marshal.OffsetOf(actionBar, "ActionBarLayout").ToInt64() == 0x288,
        "current client structs expose the native layout field at the read-only verified 0x288 offset");
    var nativeLayout = actionBar.GetField("ActionBarLayout")!.FieldType;
    var layoutNames = new[] { "Layout12X1", "Layout6X2", "Layout4X3", "Layout3X4", "Layout2X6", "Layout1X12" };
    Check(layoutNames.Select((name, code) => Convert.ToInt32(Enum.Parse(nativeLayout, name)) == code).All(valid => valid),
        "all six layout codes match the actual runtime FFXIVClientStructs enum");
    var info = type.GetProperty("Info")!.GetValue(module)!;
    var localization = type.GetProperty("Localization")!.GetValue(module)!;
    Check(info is not null && localization is not null, "compiled module instantiates and constructs localization without game services");
    NativeReadChecks.CheckCompiled(root, assembly, structs, info!, Check);
    SettingsBehaviorChecks.Compiled(root, assembly, Check);
    ItemAndFontChecks.Compiled(assembly, Check);
    NativeTooltipChecks.Compiled(assembly, Check);
    MacroDetailChecks.Compiled(assembly, Check);
    WorkshopProfileChecks.Compiled(root, assembly, Check);
    var version = FileVersionInfo.GetVersionInfo(dll);
    Check(assembly.GetName().Name == "NativeDragHotbar" && assembly.GetName().Version == new Version(2, 4, 0, 0)
        && version.FileVersion == "2.4.0.0" && version.ProductVersion == "2.4.0"
        && (string)type.GetField("Version")!.GetRawConstantValue()! == "2.4.0", "module, assembly identity, assembly version and file version agree");
    Check((string)info!.GetType().GetProperty("Author")!.GetValue(info)! == "Yuzi", "compiled module author is Yuzi");
    if (Environment.GetEnvironmentVariable("NATIVE_DRAG_HOTBAR_CHECK_REPEATER") == "1")
    {
        var repeatDll = Path.Combine(root, "build/KeyRepeater/bin/Release/net10.0-windows7.0/KeyRepeater.dll");
        var repeatAssembly = context.LoadFromAssemblyPath(repeatDll);
        var repeatType = repeatAssembly.GetType("OmniToolbox.KeyRepeaterOnline.KeyRepeater", true)!;
        host.GetType("OmniToolbox.Common.Module.Runtime.ModuleContract", true)!
            .GetMethod("ValidateType", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [repeatType]);
        Check(Activator.CreateInstance(repeatType) is not null, "compiled repeater passes actual Omni contract and instantiates without game services");
        var repeatVersion = FileVersionInfo.GetVersionInfo(repeatDll);
        Check(repeatAssembly.GetName().Name == "KeyRepeater" && repeatAssembly.GetName().Version == new Version(0, 2, 5, 0)
            && repeatVersion.FileVersion == "0.2.5.0" && repeatVersion.ProductVersion == "0.2.5"
            && (string)repeatType.GetField("Version")!.GetRawConstantValue()! == "0.2.5", "repeater actual assembly, file and module versions agree");
    }
    context.Unload();
    Console.WriteLine($"{checks} checks passed. Native game interaction still requires in-game testing.");
}
finally { Directory.Delete(temp, true); }

