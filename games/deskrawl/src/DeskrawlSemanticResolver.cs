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
        var cacheKey = $"{runtime.InstanceIdentity}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{JsonSerializer.Serialize(contract)}";
        if (Cache.TryGetValue(cacheKey, out var cached)) return cached;
        if (Cache.Count > 512) Cache.Clear();

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
                    var target = Method(parts[0], parts[1], dependency.Static, dependency.Result, dependency.Parameters);
                    if (!DeskrawlSemanticCode.HasDirectTarget(bytes, method.Pointer, variant.Instructions, target.Pointer))
                        return false;
                }
                return true;
            }
            return false;
        }
        IReadOnlyList<Il2CppRuntimeResolver.NativeMethod> Candidates(ulong klass) =>
            runtime.MethodsWithSignature(klass, isStatic, result, parameters).Where(Matches).ToArray();

        var opaque = owner.Length <= 3;
        if (!opaque)
        {
            var klass = runtime.Class(contract.Image, contract.Namespace, contract.Owner);
            return Cache[cacheKey] = DeskrawlSemanticCode.Unique(Candidates(klass), key);
        }
        if (_owners.TryGetValue(owner, out var selected))
            return Cache[cacheKey] = DeskrawlSemanticCode.Unique(Candidates(selected), key);

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
        var unique = DeskrawlSemanticCode.Unique(found, key);
        _owners[owner] = unique.klass;
        return Cache[cacheKey] = unique.method;
    }
}
