using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using PocketRecomp.Core.Analysis;
using PocketRecomp.Core.Arm;
using PocketRecomp.Core.Containers;

namespace PocketRecomp.Core.Build;

[JsonConverter(typeof(JsonStringEnumConverter<NativeBuildStatus>))]
public enum NativeBuildStatus { TranslationIncomplete, NativeLinkPending }

public sealed record TitleManifest(int SchemaVersion, string Key, string DisplayName,
    string TitleId, string ProductCode, ushort ContentVersion, string RomSha256,
    string StoredCodeSha256, string MappedCodeSha256, string NetworkPolicy);

public sealed record UnsupportedEncoding(string Encoding, int Count, string FirstAddress);

public sealed record NativeBuildReport(
    int SchemaVersion,
    NativeBuildStatus Status,
    string InputFileName,
    string RomSha256,
    string? TitleKey,
    string DisplayName,
    string ProductCode,
    string ProgramId,
    ushort ContentVersion,
    string StoredCodeSha256,
    string MappedCodeSha256,
    uint EntryPoint,
    int ReachableBlocks,
    int ReachableInstructions,
    IReadOnlyList<UnsupportedEncoding> UnsupportedEncodings,
    IReadOnlyList<string> ExternalTargets,
    bool NativeExecutableProduced,
    string NextRequiredStage);

public sealed record NativeBuildResult(NativeBuildReport Report, string ReportPath);

public static class NativeBuildPipeline
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) }
    };

    public static async Task<IReadOnlyList<TitleManifest>> LoadManifestsAsync(string directory,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(directory)) return [];
        var manifests = new List<TitleManifest>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories).Order())
        {
            await using var stream = File.OpenRead(path);
            var manifest = await JsonSerializer.DeserializeAsync<TitleManifest>(stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, cancellationToken)
                ?? throw new InvalidDataException($"Title manifest '{path}' is empty.");
            ValidateManifest(manifest, path);
            manifests.Add(manifest);
        }
        return manifests;
    }

    public static async Task<NativeBuildResult> PrepareAsync(string cartridgePath, string outputDirectory,
        IReadOnlyList<TitleManifest> manifests, CancellationToken cancellationToken = default)
    {
        var fullCartridgePath = Path.GetFullPath(cartridgePath);
        var romHash = await HashFileAsync(fullCartridgePath, cancellationToken);
        var executable = await CartridgeReader.LoadExecutableAsync(fullCartridgePath, cancellationToken);
        var title = manifests.SingleOrDefault(candidate =>
            string.Equals(candidate.RomSha256, romHash, StringComparison.OrdinalIgnoreCase));
        if (title is not null) ValidateTitleMatch(title, executable);

        var codeSet = executable.CodeSet;
        var textLength = checked((int)(codeSet.TextPages * 0x1000u));
        var graph = ControlFlowDiscoverer.Discover(executable.Code.AsSpan(0, textLength),
            codeSet.TextAddress, [codeSet.TextAddress]);
        var unsupported = graph.Blocks.Values
            .Where(block => block.EndsWithUnsupported)
            .Select(block => block.Instructions[^1])
            .GroupBy(instruction => instruction.Word)
            .Select(group => new UnsupportedEncoding($"0x{group.Key:X8}", group.Count(),
                $"0x{group.Min(item => item.Address):X8}"))
            .OrderBy(item => item.FirstAddress, StringComparer.Ordinal)
            .ToArray();
        var status = unsupported.Length == 0
            ? NativeBuildStatus.NativeLinkPending
            : NativeBuildStatus.TranslationIncomplete;
        var report = new NativeBuildReport(
            1, status, Path.GetFileName(fullCartridgePath), romHash, title?.Key,
            title?.DisplayName ?? codeSet.Name, executable.Partition.Header.ProductCode,
            $"{executable.Partition.Header.ProgramId:X16}", executable.Partition.Header.ContentVersion,
            executable.StoredCodeSha256, executable.MappedCodeSha256, codeSet.TextAddress,
            graph.Blocks.Count, graph.InstructionCount, unsupported,
            graph.UnresolvedTargets.Select(target => $"0x{target:X8}").ToArray(),
            NativeExecutableProduced: false,
            NextRequiredStage: status == NativeBuildStatus.TranslationIncomplete
                ? "Complete reachable ARM/Thumb translation coverage."
                : "Generate dispatch sources and link the native runtime.");

        var fullOutput = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(fullOutput);
        var reportPath = Path.Combine(fullOutput, "build-report.json");
        var temporaryPath = reportPath + ".tmp";
        await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(report, JsonOptions), cancellationToken);
        File.Move(temporaryPath, reportPath, overwrite: true);
        return new(report, reportPath);
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private static void ValidateManifest(TitleManifest manifest, string path)
    {
        if (manifest.SchemaVersion != 1) throw new InvalidDataException($"Title manifest '{path}' must use schemaVersion 1.");
        if (string.IsNullOrWhiteSpace(manifest.Key) || string.IsNullOrWhiteSpace(manifest.DisplayName))
            throw new InvalidDataException($"Title manifest '{path}' needs a key and displayName.");
        ValidateHash(manifest.RomSha256, "romSha256", path);
        ValidateHash(manifest.StoredCodeSha256, "storedCodeSha256", path);
        ValidateHash(manifest.MappedCodeSha256, "mappedCodeSha256", path);
    }

    private static void ValidateHash(string hash, string field, string path)
    {
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit))
            throw new InvalidDataException($"Title manifest '{path}' has an invalid {field}.");
    }

    private static void ValidateTitleMatch(TitleManifest title, ExecutableImage executable)
    {
        var header = executable.Partition.Header;
        if (!string.Equals(title.ProductCode, header.ProductCode, StringComparison.Ordinal) ||
            !string.Equals(title.TitleId, $"{header.ProgramId:X16}", StringComparison.OrdinalIgnoreCase) ||
            title.ContentVersion != header.ContentVersion ||
            !string.Equals(title.StoredCodeSha256, executable.StoredCodeSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(title.MappedCodeSha256, executable.MappedCodeSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Cartridge content does not match title manifest '{title.Key}'.");
    }
}
