using System.Collections.Concurrent;
using System.IO;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using GameValueEditor.Modules.Runtime;

namespace GameValueEditor.Modules.Deskrawl;

internal sealed class DeskrawlSemanticResolver(Il2CppRuntimeResolver runtime, string path)
{
    private sealed record CodeVariant(int Instructions, string Hash, string[]? RequiredTargets = null);
    private sealed record Contract(string Role, string Image, string Namespace, string Owner,
        bool Static, string Result, string[] Parameters, int Instructions, string Hash, string? LegacyHash,
        CodeVariant[]? ReviewedVariants = null, string[]? RequiredTargets = null);

    private static readonly IReadOnlyDictionary<string, Contract> Contracts = Load();
    private static readonly ConcurrentDictionary<string, Il2CppRuntimeResolver.NativeMethod> Cache = new();
    private static readonly ConcurrentDictionary<string, byte> Rejected = new();
    private readonly Dictionary<string, ulong> _owners = new();

    private static IReadOnlyDictionary<string, Contract> Load()
    {
        using var stream = typeof(DeskrawlSemanticResolver).Assembly.GetManifestResourceStream("Deskrawl.SemanticContracts.json")!;
        return JsonSerializer.Deserialize<Contract[]>(stream)!.ToDictionary(item => item.Role, StringComparer.Ordinal);
    }

    internal Il2CppRuntimeResolver.NativeMethod Method(string owner, string role, bool isStatic, string result, string[] parameters)
    {
        runtime.CheckAlive();
        var key = owner + "|" + role;
        if (!Contracts.TryGetValue(key, out var contract) || contract.Static != isStatic ||
            contract.Result != result || !contract.Parameters.SequenceEqual(parameters))
            throw new InvalidOperationException($"{key} 不在已分析语义契约中；未执行修改。");
        // File identity plus loaded instance: never reuse across rebuilds or restarts.
        var info = new FileInfo(path);
        var cacheKey = $"{runtime.InstanceIdentity}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{ProofIdentity(key, new())}";
        if (Cache.TryGetValue(cacheKey, out var cached)) return cached;
        if (Rejected.ContainsKey(cacheKey))
            throw new InvalidOperationException($"{key} 的完整语义候选缺失或不唯一；未执行修改。");
        if (Cache.Count > 512) Cache.Clear();
        if (Rejected.Count > 512) Rejected.Clear();

        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        bool Matches(Il2CppRuntimeResolver.NativeMethod method)
        {
            var block = pe.GetSectionData(checked((int)(method.Pointer - runtime.ModuleBase)));
            var variants = new List<CodeVariant> { new(contract.Instructions, contract.Hash, contract.RequiredTargets) };
            if (contract.LegacyHash is not null) variants.Add(new(contract.Instructions, contract.LegacyHash, contract.RequiredTargets));
            variants.AddRange(contract.ReviewedVariants ?? []);
            foreach (var variant in variants)
            {
                var bytes = block.GetContent(0, Math.Min(block.Length, variant.Instructions * 15)).ToArray();
                if (DeskrawlSemanticCode.Fingerprint(bytes, method.Pointer, variant.Instructions,
                        runtime.ModuleBase, runtime.ModuleEnd) != variant.Hash) continue;
                // Rewritten XP code delegates part of its formula. Prove those
                // callees and the actual call/tail-call edges as well as this body.
                foreach (var targetRole in variant.RequiredTargets ?? [])
                {
                    var dependency = Contracts[targetRole];
                    var parts = targetRole.Split('|');
                    var target = targetRole == key ? method :
                        Method(parts[0], parts[1], dependency.Static, dependency.Result, dependency.Parameters);
                    if (!DeskrawlSemanticCode.HasDirectTarget(bytes, method.Pointer, variant.Instructions, target.Pointer))
                        return false;
                }
                return true;
            }
            return false;
        }
        IReadOnlyList<Il2CppRuntimeResolver.NativeMethod> Candidates(ulong klass) =>
            // IL2CPP may fold multiple metadata aliases in this class onto one
            // native body. Keep distinct targets (and distinct classes) ambiguous.
            runtime.MethodsWithSignature(klass, isStatic, result, parameters).Where(Matches)
                .DistinctBy(method => method.Pointer).ToArray();
        T UniqueProof<T>(IReadOnlyList<T> candidates)
        {
            // Only cache a completed candidate search, never a runtime/IO error.
            // The key includes this process instance and the entire proof graph.
            if (candidates.Count != 1) Rejected[cacheKey] = 0;
            return DeskrawlSemanticCode.Unique(candidates, key);
        }

        var opaque = owner.Length <= 3;
        if (!opaque)
        {
            var klass = runtime.Class(contract.Image, contract.Namespace, contract.Owner);
            return Cache[cacheKey] = UniqueProof(Candidates(klass));
        }
        if (_owners.TryGetValue(owner, out var selected))
            return Cache[cacheKey] = UniqueProof(Candidates(selected));

        // The observed alias is a fast candidate only. Its full method proof must
        // pass; otherwise search bounded opaque types, never advance a name/ordinal.
        try
        {
            var hint = runtime.Class(contract.Image, contract.Namespace, contract.Owner);
            var matches = Candidates(hint);
            if (matches.Count == 1)
            {
                _owners[owner] = hint;
                return Cache[cacheKey] = matches[0];
            }
            if (matches.Count > 1) throw new InvalidOperationException("语义候选不唯一。");
        }
        catch (InvalidOperationException) { /* Missing/changed alias: do not call it. */ }
        var found = runtime.OpaqueClasses(contract.Image)
            .SelectMany(klass => Candidates(klass).Select(method => (klass, method))).ToArray();
        var unique = UniqueProof(found);
        _owners[owner] = unique.klass;
        return Cache[cacheKey] = unique.method;
    }

    private static string ProofIdentity(string role, HashSet<string> visited)
    {
        if (!visited.Add(role)) return "";
        var contract = Contracts[role];
        var targets = (contract.RequiredTargets ?? []).Concat(
            (contract.ReviewedVariants ?? []).SelectMany(variant => variant.RequiredTargets ?? []))
            .Distinct().Order(StringComparer.Ordinal);
        return JsonSerializer.Serialize(contract) + string.Concat(targets.Select(target => ProofIdentity(target, visited)));
    }
}
