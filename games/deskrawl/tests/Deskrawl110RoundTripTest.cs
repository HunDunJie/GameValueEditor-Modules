using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using GameValueEditor.ModuleSdk;
using GameValueEditor.Modules.Deskrawl;

namespace GameValueEditor.Modules.LiveTests;

// Explicit live acceptance only. Requires an independently captured save backup.
public sealed class Deskrawl110RoundTripTest : IModuleLiveTest
{
    public string Game => "deskrawl-110-roundtrip";

    public async Task RunAsync(string[] args)
    {
        var backup = args.Single(a => a.StartsWith("--backup=", StringComparison.Ordinal))[9..];
        LiveTestSupport.Assert(File.Exists(backup) && new FileInfo(backup).Length > 4,
            "Create a save backup before running reversible business writes.");
        using var live = Process.GetProcessesByName("Deskrawl").Single();
        var (process, build) = await LiveTestSupport.CreateContextAsync(live);
        var adapter = new DeskrawlGameAdapter();
        LiveTestSupport.Assert(adapter.GetCompatibilityDiagnostics(process, build)
            .Count(d => d.Status == GameCompatibilityDiagnosticStatus.Passed) == 8,
            "All capabilities must validate before round trips.");
        long Read(string key) => long.Parse(adapter.ReadField(process, key).DisplayValue, CultureInfo.InvariantCulture);
        void Write(string key, long target)
        {
            var result = adapter.WriteField(process, key, target.ToString(CultureInfo.InvariantCulture));
            var actual = long.Parse(result.DisplayValue, CultureInfo.InvariantCulture);
            LiveTestSupport.Assert(actual == target || (key.EndsWith("|gold", StringComparison.Ordinal) && actual >= target),
                "Write result is uncertain; do not repeat automatically.");
        }
        void DeltaRoundTrip(string key, long delta)
        {
            var before = Read(key);
            var confirmed = false;
            try
            {
                Write(key, checked(before + delta));
                confirmed = true;
                LiveTestSupport.Assert(Read(key) >= before + delta, "Authority readback rejected the test delta.");
            }
            finally
            {
                if (confirmed) Write(key, checked(Read(key) - delta));
            }
        }
        if (args.Contains("--loadout-character-fields", StringComparer.Ordinal) || args.Contains("--loadout-snapshot-only", StringComparer.Ordinal))
        {
            VerifyLoadoutCharacterFields(adapter, process, args.Contains("--loadout-snapshot-only", StringComparer.Ordinal));
            if (args.Contains("--loadout-character-only", StringComparer.Ordinal) || args.Contains("--loadout-snapshot-only", StringComparer.Ordinal)) return;
        }
        if (!args.Contains("--stats-only", StringComparer.Ordinal))
        {
            var gold = ModuleFieldKey.Create("game.deskrawl.character-attributes", "current-character", "gold");
            DeltaRoundTrip(gold, Read(gold) == int.MaxValue ? -1 : 1);
            Console.WriteLine("Gold +1/-1 test delta restored through verified main-thread/save path.");

            var materials = adapter.ReadInventory(process);
            var material = materials.Where(m => m.Count > 1).OrderByDescending(m => m.FieldKey == "ore1").First();
            DeltaRoundTrip(material.FieldKey, -1);
            var stackableReader = typeof(DeskrawlGameAdapter).GetMethod("ReadStackableInventory", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var stackables = (IReadOnlyList<AdapterInventoryItem>)stackableReader.Invoke(adapter, [process])!;
            var stackable = stackables.Where(m => m.Count > 1).OrderByDescending(m => m.FieldKey.Contains("CommonFish", StringComparison.Ordinal)).First();
            DeltaRoundTrip(stackable.FieldKey, -1);
            Console.WriteLine("Material and owned stackable -1/+1 deltas restored; incoming rewards preserved.");

            foreach (var (editor, entity, field, target) in new[]
            {
                ("equipment-generation", "generation-rules", "maximum-rolls", 1L),
                ("fishing-loot", "fishing-rules", "success-rate", 100L),
                ("drop-rules", "drop-rules", "equipment-drop-rate", 100L)
            })
            {
                var key = ModuleFieldKey.Create("game.deskrawl." + editor, entity, field);
                var original = Read(key);
                try { Write(key, target); LiveTestSupport.Assert(Read(key) == target, "Rule readback failed."); }
                finally { Write(key, original); }
                LiveTestSupport.Assert(Read(key) == original, "Rule restoration did not verify.");
            }
            var special = ModuleFieldKey.Create("game.deskrawl.drop-rules", "drop-rules", "offline-tradable-drop-multiplier");
            var initialSpecial = adapter.ReadField(process, special);
            var restoreSpecial = initialSpecial.Status.Contains("游戏当前", StringComparison.Ordinal) ? -1 : long.Parse(initialSpecial.DisplayValue, CultureInfo.InvariantCulture);
            try { Write(special, long.Parse(initialSpecial.DisplayValue, CultureInfo.InvariantCulture)); }
            finally { adapter.WriteField(process, special, restoreSpecial.ToString(CultureInfo.InvariantCulture)); }
            LiveTestSupport.Assert(adapter.ReadField(process, special).DisplayValue == initialSpecial.DisplayValue,
                "Special multiplier restoration failed.");
            if (restoreSpecial == -1)
                LiveTestSupport.Assert(adapter.ReadField(process, special).Status.Contains("游戏当前", StringComparison.Ordinal),
                    "Restoring the formula left an explicit special multiplier override.");
            Console.WriteLine("Equipment, fishing and both drop rules exercised and restored.");
        }

        foreach (var key in new[] {
            ModuleFieldKey.Create("game.deskrawl.equipment-generation", "generation-rules", "maximum-rolls"),
            ModuleFieldKey.Create("game.deskrawl.fishing-loot", "fishing-rules", "success-rate"),
            ModuleFieldKey.Create("game.deskrawl.drop-rules", "drop-rules", "equipment-drop-rate"),
            ModuleFieldKey.Create("game.deskrawl.drop-rules", "drop-rules", "offline-tradable-drop-multiplier") })
            Console.WriteLine($"Rule state: {key} = {adapter.ReadField(process, key).DisplayValue}; {adapter.ReadField(process, key).Status}");

        // Check all final-stat write routes, then remove only this test's exact
        // owned entry jump. Do not leave an original-value override freezing stats.
        var runtimeType = typeof(DeskrawlGameAdapter).Assembly.GetType("GameValueEditor.Modules.Deskrawl.DeskrawlRuntime", true)!;
        using var runtime = (IDisposable)Activator.CreateInstance(runtimeType, BindingFlags.Instance | BindingFlags.NonPublic, null, [process], null)!;
        object Invoke(string name, params object?[] values) => runtimeType.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(m => m.Name == name && m.GetParameters().Length == values.Length).Invoke(runtime, values)!;
        var method = Invoke("Method", "Player", "bjy", false, "System.Double", new[] { "StatType" });
        var pointer = (ulong)method.GetType().GetProperty("Pointer")!.GetValue(method)!;
        var patch = Invoke("CreateEntryPatch", pointer, 14);
        var originalBytes = (byte[])patch.GetType().GetProperty("Original")!.GetValue(patch)!;
        var resolver = runtimeType.GetField("_runtime", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtime)!;
        byte[] ReadEntry() => (byte[])resolver.GetType().GetMethod("Read")!.Invoke(resolver, [pointer, originalBytes.Length])!;
        LiveTestSupport.Assert(ReadEntry().SequenceEqual(originalBytes), "An existing stat override must be preserved; stop before stat tests.");
        var fields = adapter.ReadEditorEntities(process, "game.deskrawl.character-attributes").Single().Fields;
        var checkedStats = 0;
        try
        {
            foreach (var key in new[] { "strength", "dexterity", "intelligence", "gold-find", "item-find-rate", "hp-potion-find", "material-find", "xp-gain" })
            {
                Write(ModuleFieldKey.Create("game.deskrawl.character-attributes", "current-character", key), fields.Single(f => f.Key == key).Value);
                checkedStats++;
            }
        }
        finally
        {
            var dictionary = (System.Collections.IDictionary)runtimeType.GetField("StatOverrides", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            if (dictionary[process.ProcessId] is { } state)
            {
                var remote = (ulong)state.GetType().GetProperty("RemoteBase")!.GetValue(state)!;
                var hookType = typeof(DeskrawlGameAdapter).Assembly.GetType("GameValueEditor.Modules.Runtime.Il2CppMainThreadHook", true)!;
                var owned = (byte[])hookType.GetMethod("Jump")!.Invoke(null, [remote, originalBytes.Length])!;
                resolver.GetType().GetMethod("CheckAlive")!.Invoke(resolver, null);
                LiveTestSupport.Assert(ReadEntry().SequenceEqual(owned), "Stat jump ownership changed; do not overwrite another tool.");
                Invoke("RestorePatch", patch, true);
                dictionary.Remove(process.ProcessId);
            }
        }
        LiveTestSupport.Assert(ReadEntry().SequenceEqual(originalBytes), "Stat entry bytes were not restored.");
        Console.WriteLine($"Final-stat routes checked: {checkedStats}; exact original function entry restored. This is consumer readback, not combat-effect or restart evidence.");
    }

    private static void VerifyLoadoutCharacterFields(DeskrawlGameAdapter adapter, GameProcessContext process, bool snapshotOnly)
    {
        var type = typeof(DeskrawlGameAdapter).Assembly.GetType("GameValueEditor.Modules.Deskrawl.DeskrawlRuntime", true)!;
        using var runtime = (IDisposable)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null, [process], null)!;
        var native = type.GetField("_runtime", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtime)!;
        object Q(string name, params object?[] values) => native.GetType().GetMethods()
            .Single(m => m.Name == name && m.GetParameters().Length == values.Length).Invoke(native, values)!;
        ulong Ptr(ulong address) => (ulong)Q("ReadPointer", address);
        int Int(ulong address) => (int)Q("ReadInt", address);
        var talent = (ulong)type.GetProperty("TalentBook", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtime)!;
        string Snapshot()
        {
            var array = Ptr(talent + 0x28);
            LiveTestSupport.Assert(Ptr(array + 0x18) == 3, "Expected three talent loadouts.");
            var rows = new List<string> { Int(talent + 0x30).ToString(CultureInfo.InvariantCulture) };
            for (var index = 0; index < 3; index++)
            {
                var dictionary = Ptr(array + 0x20 + (ulong)index * 8);
                var klass = (ulong)Q("ObjectClass", dictionary);
                var count = Int((ulong)Q("Address", dictionary, "_count", "System.Int32"));
                var free = Int((ulong)Q("Address", dictionary, "_freeCount", "System.Int32"));
                LiveTestSupport.Assert(count is >= 0 and <= 10000 && free >= 0 && free <= count, "Invalid talent allocation count.");
                var entriesType = (string)Q("FieldType", klass, "_entries");
                var entries = (ulong)Q("Reference", dictionary, "_entries", entriesType);
                var bytes = Array.Empty<byte>();
                if (count > 0)
                {
                    LiveTestSupport.Assert(entries != 0 && (ulong)count <= Ptr(entries + 0x18), "Talent entries exceed capacity.");
                    var arrayClass = (ulong)Q("ObjectClass", entries);
                    var size = (ulong)Q("Call", Q("Export", "il2cpp_array_element_size"), arrayClass, 0UL, 0UL, 0UL);
                    LiveTestSupport.Assert(size is >= 16 and <= 128, "Unexpected talent entry size.");
                    bytes = (byte[])Q("Read", entries + 0x20, checked(count * (int)size));
                }
                rows.Add($"{count}:{free}:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)));
            }
            return string.Join("|", rows);
        }
        string Key(string field) => ModuleFieldKey.Create("game.deskrawl.character-attributes", "current-character", field);
        long Read(string field) => long.Parse(adapter.ReadField(process, Key(field)).DisplayValue, CultureInfo.InvariantCulture);
        void Write(string field, long target) => LiveTestSupport.Assert(
            long.Parse(adapter.WriteField(process, Key(field), target.ToString(CultureInfo.InvariantCulture)).DisplayValue,
                CultureInfo.InvariantCulture) == target, "Uncertain character write; do not resend.");
        var before = Snapshot();
        var points = Read("talent-points");
        var player = (ulong)type.GetProperty("Player", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtime)!;
        var primary = (ulong)Q("Address", player, "BasePrimaryStat", "CodeStage.AntiCheat.ObscuredTypes.ObscuredFloat");
        var readPrimary = type.GetMethod("ReadObscuredFloat", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var originalPrimary = (float)readPrimary.Invoke(runtime, [primary])!;
        void PrintSnapshot() => Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new {
            TalentPoints = Read("talent-points"), ParagonPoints = Read("paragon-points"), Primary = (float)readPrimary.Invoke(runtime, [primary])!,
            ActiveLoadout = int.Parse(before.Split('|')[0], CultureInfo.InvariantCulture),
            AllocatedCounts = before.Split('|').Skip(1).Select(row => { var parts = row.Split(':'); return int.Parse(parts[0]) - int.Parse(parts[1]); }).ToArray(),
            ProcessId = process.ProcessId, ReadOnlySnapshot = snapshotOnly }));
        if (snapshotOnly) { PrintSnapshot(); return; }
        var confirmed = false;
        var delta = points == int.MaxValue ? -1L : 1L;
        try
        {
            Write("talent-points", points + delta);
            confirmed = true;
            LiveTestSupport.Assert(Read("talent-points") == points + delta && Snapshot() == before,
                "Talent balance changed allocations or selected loadout.");
        }
        finally { if (confirmed) Write("talent-points", Read("talent-points") - delta); }
        LiveTestSupport.Assert(Snapshot() == before, "Talent restoration changed a loadout.");

        // Exercise the new level/XP setter without crossing a level boundary.
        // Its native ensure-points step can top up a previously edited balance;
        // restore that side effect through the verified point-change/save path.
        var experience = Read("experience");
        var originalPoints = Read("talent-points");
        Write("experience", experience);
        if (Read("talent-points") != originalPoints) Write("talent-points", originalPoints);
        LiveTestSupport.Assert(Snapshot() == before, "XP refresh changed allocated talent loadouts.");
        Write("paragon-points", Read("paragon-points"));
        type.GetMethod("WriteBasePrimary", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(runtime, [player, originalPrimary]);
        LiveTestSupport.Assert((float)readPrimary.Invoke(runtime, [primary])! == originalPrimary,
            "Primary-stat constructor/save path changed the original floating-point value.");
        Console.WriteLine("Talent +1/-1 restored; all three allocation dictionaries and active selection unchanged. Same-level XP/save, existing paragon and exact primary-stat value verified.");
        PrintSnapshot();
    }
}
