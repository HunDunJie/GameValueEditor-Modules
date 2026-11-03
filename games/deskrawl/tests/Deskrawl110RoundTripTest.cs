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
            LiveTestSupport.Assert(long.Parse(result.DisplayValue, CultureInfo.InvariantCulture) == target,
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
}
