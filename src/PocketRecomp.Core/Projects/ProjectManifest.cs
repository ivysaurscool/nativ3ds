using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PocketRecomp.Core.Projects;

public sealed class ProjectManifest
{
    public int FormatVersion { get; init; }
    public string Name { get; init; } = "";
    public List<SegmentManifest> Segments { get; init; } = [];
}

public sealed class SegmentManifest
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public string GuestAddress { get; init; } = "";
    public string Mode { get; init; } = "arm";
    public List<string> EntryPoints { get; init; } = [];
    public string? Sha256 { get; init; }
}

public sealed record LoadedSegment(string Name, string FilePath, uint GuestAddress,
    IReadOnlyList<uint> EntryPoints, byte[] Code, string Sha256);

public sealed record LoadedProject(string Name, string ManifestPath, IReadOnlyList<LoadedSegment> Segments);

public static class ProjectLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static async Task<LoadedProject> LoadAsync(string manifestPath, CancellationToken cancellationToken = default)
    {
        var fullManifestPath = System.IO.Path.GetFullPath(manifestPath);
        var projectDirectory = System.IO.Path.GetDirectoryName(fullManifestPath)
            ?? throw new InvalidDataException("Manifest must have a parent directory.");
        await using var stream = File.OpenRead(fullManifestPath);
        var manifest = await JsonSerializer.DeserializeAsync<ProjectManifest>(stream, Options, cancellationToken)
            ?? throw new InvalidDataException("Manifest is empty.");

        if (manifest.FormatVersion != 1) throw new InvalidDataException("formatVersion must be 1.");
        if (string.IsNullOrWhiteSpace(manifest.Name)) throw new InvalidDataException("Project name is required.");
        if (manifest.Segments.Count == 0) throw new InvalidDataException("At least one code segment is required.");

        var loaded = new List<LoadedSegment>();
        foreach (var segment in manifest.Segments)
        {
            if (string.IsNullOrWhiteSpace(segment.Name)) throw new InvalidDataException("Every segment needs a name.");
            if (!string.Equals(segment.Mode, "arm", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Segment '{segment.Name}' uses unsupported mode '{segment.Mode}'. Milestone 0.2 supports ARM state only.");
            if (System.IO.Path.IsPathRooted(segment.Path))
                throw new InvalidDataException($"Segment '{segment.Name}' path must be relative to the manifest.");

            var fullPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(projectDirectory, segment.Path));
            var relative = System.IO.Path.GetRelativePath(projectDirectory, fullPath);
            if (relative == ".." || relative.StartsWith(".." + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidDataException($"Segment '{segment.Name}' path escapes the project directory.");

            var code = await File.ReadAllBytesAsync(fullPath, cancellationToken);
            if ((code.Length & 3) != 0) throw new InvalidDataException($"Segment '{segment.Name}' size must be a multiple of four for ARM state.");
            var baseAddress = ParseAddress(segment.GuestAddress, $"segment '{segment.Name}' guestAddress");
            if ((baseAddress & 3) != 0) throw new InvalidDataException($"Segment '{segment.Name}' guestAddress must be aligned.");
            var entries = segment.EntryPoints.Count == 0
                ? [baseAddress]
                : segment.EntryPoints.Select(value => ParseAddress(value, $"segment '{segment.Name}' entry point")).ToList();
            var digest = Convert.ToHexString(SHA256.HashData(code)).ToLowerInvariant();
            if (segment.Sha256 is not null && !string.Equals(NormalizeHash(segment.Sha256), digest, StringComparison.Ordinal))
                throw new InvalidDataException($"Segment '{segment.Name}' SHA-256 does not match its manifest.");
            loaded.Add(new(segment.Name, fullPath, baseAddress, entries, code, digest));
        }
        return new(manifest.Name, fullManifestPath, loaded);
    }

    public static uint ParseAddress(string text, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException($"{fieldName} is required.");
        var digits = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
        return uint.TryParse(digits, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value : throw new InvalidDataException($"{fieldName} must be a hexadecimal 32-bit address.");
    }

    private static string NormalizeHash(string hash) => hash.Replace("-", "", StringComparison.Ordinal).Trim().ToLowerInvariant();
}
