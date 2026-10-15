using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CalendarRemediation.Core.Feasibility;

namespace CalendarRemediation.Host.Feasibility;

public static class EvidenceFiles
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public static T Read<T>(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > 32 * 1024 * 1024) { throw new InvalidDataException("Evidence file exceeds 32 MiB."); }
        return JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), JsonOptions)
            ?? throw new JsonException("Evidence cannot be null.");
    }

    // CreateNew makes output paths single-use, including mutation intent files.
    public static void Write<T>(string path, T value)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, value, JsonOptions);
        stream.Flush(flushToDisk: true);
    }

    public static ArtifactReference Reference(string path, string relativeTo)
    {
        if (new FileInfo(path).Length > 64 * 1024 * 1024) { throw new InvalidDataException("Artifact exceeds 64 MiB."); }
        using var stream = File.OpenRead(path);
        return new(Path.GetRelativePath(relativeTo, path), Convert.ToHexStringLower(SHA256.HashData(stream)));
    }

    public static GateAssessment Assess(string evidencePath, DateTimeOffset now)
    {
        var evidence = Read<FeasibilityEvidence>(evidencePath);
        if (evidence.FeasibilityDecision is null || evidence.RuleEvidenceManifest is null
            || evidence.WorkloadProfile is null || evidence.CategoryAdmissionMatrix is null)
        {
            return new(ProductVerdict.Blocked, false, [], ["All four evidence records are required."]);
        }
        var root = Path.GetDirectoryName(Path.GetFullPath(evidencePath))!;
        var references = (evidence.FeasibilityDecision.Claims ?? []).Where(c => c is not null).SelectMany(c => c.Artifacts ?? [])
            .Concat(evidence.RuleEvidenceManifest.AuthoritativeSources ?? [])
            .Concat(evidence.RuleEvidenceManifest.SourceRules ?? [])
            .Concat(evidence.RuleEvidenceManifest.TargetRules ?? [])
            .Concat(evidence.RuleEvidenceManifest.ProviderMappings ?? [])
            .Concat(evidence.WorkloadProfile.Measurements ?? [])
            .Concat((evidence.CategoryAdmissionMatrix.Categories ?? []).Where(c => c is not null)
                .SelectMany(c => c.Claims ?? []).Where(c => c is not null).SelectMany(c => c.Artifacts ?? []));
        var verified = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in references)
        {
            if (reference is null || string.IsNullOrWhiteSpace(reference.Path)) { continue; }
            if (Path.IsPathRooted(reference.Path)) { continue; }
            var path = Path.GetFullPath(Path.Combine(root, reference.Path));
            var relative = Path.GetRelativePath(root, path);
            if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) { continue; }
            try
            {
                if (HasLink(root, relative)) { continue; }
                if (new FileInfo(path).Length > 64 * 1024 * 1024) { continue; }
                using var stream = File.OpenRead(path);
                if (Convert.ToHexStringLower(SHA256.HashData(stream)) == reference.Sha256)
                {
                    verified.Add(FeasibilityGate.ArtifactKey(reference));
                }
            }
            catch (IOException) { /* Missing artifacts remain unverified and block gates. */ }
            catch (UnauthorizedAccessException) { /* Unreadable artifacts remain unverified. */ }
        }
        return FeasibilityGate.Assess(evidence, now, verified);
    }

    private static bool HasLink(string root, string relative)
    {
        var current = root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) { return true; }
        }
        return false;
    }
}
