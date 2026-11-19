using System.Buffers.Binary;
using System.Text;

namespace PocketRecomp.Core.Containers;

public enum ThreeDsContainerKind { Unknown, NcsdCartridge, NcchContent }

public sealed record ThreeDsContainerInfo(
    string FilePath,
    ThreeDsContainerKind Kind,
    long FileSize,
    ulong? DeclaredSize,
    bool SizeMatches,
    string Magic);

/// <summary>
/// Identifies 3DS container headers without decrypting, extracting, or accepting keys.
/// </summary>
public static class ThreeDsContainer
{
    private const int MediaUnit = 0x200;

    public static async Task<ThreeDsContainerInfo> InspectAsync(string path, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length < 0x104)
            return new(fullPath, ThreeDsContainerKind.Unknown, stream.Length, null, false, "");

        var header = new byte[0x108];
        var read = 0;
        while (read < header.Length)
        {
            var chunk = await stream.ReadAsync(header.AsMemory(read), cancellationToken);
            if (chunk == 0) break;
            read += chunk;
        }
        if (read < 0x104)
            return new(fullPath, ThreeDsContainerKind.Unknown, stream.Length, null, false, "");

        var magic = Encoding.ASCII.GetString(header, 0x100, 4);
        var kind = magic switch
        {
            "NCSD" => ThreeDsContainerKind.NcsdCartridge,
            "NCCH" => ThreeDsContainerKind.NcchContent,
            _ => ThreeDsContainerKind.Unknown
        };
        ulong? declaredSize = null;
        if (kind != ThreeDsContainerKind.Unknown && read >= 0x108)
            declaredSize = (ulong)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x104, 4)) * MediaUnit;
        var matches = declaredSize is not null && declaredSize.Value <= (ulong)stream.Length;
        return new(fullPath, kind, stream.Length, declaredSize, matches, SanitizeMagic(magic));
    }

    private static string SanitizeMagic(string magic) =>
        string.Concat(magic.Select(character => char.IsAsciiLetterOrDigit(character) ? character : '.'));
}
