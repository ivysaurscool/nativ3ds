using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using PocketRecomp.Core.Compression;

namespace PocketRecomp.Core.Containers;

public sealed record CartridgePartition(int Index, ulong Offset, ulong Size, NcchHeader Header);

public sealed record NcchHeader(
    ulong PartitionId,
    ulong ProgramId,
    ushort ContentVersion,
    string ProductCode,
    uint ExtendedHeaderSize,
    byte[] Flags,
    uint MediaUnitSize,
    ulong ContentSize,
    ulong ExeFsOffset,
    ulong ExeFsSize,
    ulong RomFsOffset,
    ulong RomFsSize)
{
    public bool NoCrypto => (Flags[7] & 0x04) != 0;
}

public sealed record CartridgeImage(string FilePath, long FileSize, ulong MediaId,
    IReadOnlyList<CartridgePartition> Partitions);

public sealed record CodeSetInfo(string Name, bool Compressed, ushort RemasterVersion,
    uint TextAddress, uint TextPages, uint TextSize,
    uint RoAddress, uint RoPages, uint RoSize,
    uint DataAddress, uint DataPages, uint DataSize,
    uint BssSize, uint StackSize)
{
    public uint MappedCodeSize => checked((TextPages + RoPages + DataPages) * 0x1000u);
}

public sealed record ExeFsEntry(string Name, uint Offset, uint Size, ulong AbsoluteOffset);

public sealed record ExecutableImage(CartridgeImage Cartridge, CartridgePartition Partition,
    CodeSetInfo CodeSet, IReadOnlyList<ExeFsEntry> ExeFsEntries, byte[] Code,
    string StoredCodeSha256, string MappedCodeSha256);

public static class CartridgeReader
{
    private const uint NcsdMediaUnit = 0x200;
    private const int HeaderSize = 0x200;
    private const int MaxCodeFileSize = 64 * 1024 * 1024;

    public static async Task<CartridgeImage> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        await using var stream = Open(fullPath);
        var ncsd = await ReadAtAsync(stream, 0, HeaderSize, cancellationToken);
        RequireMagic(ncsd, 0x100, "NCSD", "Cartridge does not contain an NCSD header.");
        var declaredSize = (ulong)U32(ncsd, 0x104) * NcsdMediaUnit;
        if (declaredSize > (ulong)stream.Length) throw new InvalidDataException("NCSD declared size exceeds the file.");
        var mediaId = U64(ncsd, 0x108);
        var partitions = new List<CartridgePartition>();

        for (var index = 0; index < 8; index++)
        {
            var tableOffset = 0x120 + index * 8;
            var offset = (ulong)U32(ncsd, tableOffset) * NcsdMediaUnit;
            var size = (ulong)U32(ncsd, tableOffset + 4) * NcsdMediaUnit;
            if (size == 0) continue;
            ValidateRange(offset, size, (ulong)stream.Length, $"NCSD partition {index}");
            var headerBytes = await ReadAtAsync(stream, offset, HeaderSize, cancellationToken);
            RequireMagic(headerBytes, 0x100, "NCCH", $"NCSD partition {index} has no NCCH header.");
            var flags = headerBytes.AsSpan(0x188, 8).ToArray();
            if (flags[6] > 7) throw new InvalidDataException($"NCCH partition {index} has an invalid media-unit exponent.");
            var unit = checked(NcsdMediaUnit << flags[6]);
            var contentSize = (ulong)U32(headerBytes, 0x104) * unit;
            if (contentSize > size) throw new InvalidDataException($"NCCH partition {index} content exceeds its NCSD partition.");
            var productCode = Encoding.ASCII.GetString(headerBytes, 0x150, 16).TrimEnd('\0');
            var ncch = new NcchHeader(
                U64(headerBytes, 0x108), U64(headerBytes, 0x118), U16(headerBytes, 0x112),
                productCode, U32(headerBytes, 0x180), flags, unit, contentSize,
                (ulong)U32(headerBytes, 0x1A0) * unit, (ulong)U32(headerBytes, 0x1A4) * unit,
                (ulong)U32(headerBytes, 0x1B0) * unit, (ulong)U32(headerBytes, 0x1B4) * unit);
            ValidateOptionalRegion(ncch.ExeFsOffset, ncch.ExeFsSize, contentSize, $"NCCH partition {index} ExeFS");
            ValidateOptionalRegion(ncch.RomFsOffset, ncch.RomFsSize, contentSize, $"NCCH partition {index} RomFS");
            partitions.Add(new(index, offset, size, ncch));
        }

        if (partitions.Count == 0) throw new InvalidDataException("NCSD contains no partitions.");
        return new(fullPath, stream.Length, mediaId, partitions);
    }

    public static async Task<ExecutableImage> LoadExecutableAsync(string path, CancellationToken cancellationToken = default)
    {
        var cartridge = await ReadAsync(path, cancellationToken);
        var partition = cartridge.Partitions.FirstOrDefault(candidate =>
            candidate.Header.ExtendedHeaderSize > 0 && candidate.Header.ExeFsSize >= HeaderSize)
            ?? throw new InvalidDataException("Cartridge has no executable NCCH partition.");
        if (!partition.Header.NoCrypto)
            throw new InvalidDataException("Executable NCCH is encrypted. nativ3ds does not accept keys or decrypt protected content.");

        await using var stream = Open(cartridge.FilePath);
        var extendedHeader = await ReadAtAsync(stream, partition.Offset + HeaderSize,
            checked((int)partition.Header.ExtendedHeaderSize), cancellationToken);
        if (extendedHeader.Length < 0x40) throw new InvalidDataException("NCCH extended header is too short for code-set metadata.");
        var codeSet = ParseCodeSet(extendedHeader);
        var entries = await ReadExeFsAsync(stream, partition, cancellationToken);
        var codeEntry = entries.FirstOrDefault(entry => entry.Name == ".code")
            ?? throw new InvalidDataException("Executable ExeFS has no .code entry.");
        if (codeEntry.Size > MaxCodeFileSize) throw new InvalidDataException("ExeFS .code exceeds the 64 MiB safety limit.");
        var storedCode = await ReadAtAsync(stream, codeEntry.AbsoluteOffset, checked((int)codeEntry.Size), cancellationToken);
        var storedHash = Convert.ToHexString(SHA256.HashData(storedCode)).ToLowerInvariant();
        var mappedCode = codeSet.Compressed ? BackwardLz.Decompress(storedCode) : storedCode;
        if ((uint)mappedCode.Length != codeSet.MappedCodeSize)
            throw new InvalidDataException($"Mapped .code size 0x{mappedCode.Length:X} does not match exheader size 0x{codeSet.MappedCodeSize:X}.");
        var mappedHash = Convert.ToHexString(SHA256.HashData(mappedCode)).ToLowerInvariant();
        return new(cartridge, partition, codeSet, entries, mappedCode, storedHash, mappedHash);
    }

    private static async Task<IReadOnlyList<ExeFsEntry>> ReadExeFsAsync(FileStream stream,
        CartridgePartition partition, CancellationToken cancellationToken)
    {
        var exefs = partition.Header.ExeFsOffset;
        var exefsSize = partition.Header.ExeFsSize;
        var header = await ReadAtAsync(stream, partition.Offset + exefs, HeaderSize, cancellationToken);
        var dataSize = exefsSize - HeaderSize;
        var entries = new List<ExeFsEntry>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < 10; index++)
        {
            var offset = index * 16;
            var name = Encoding.ASCII.GetString(header, offset, 8).TrimEnd('\0');
            var fileOffset = U32(header, offset + 8);
            var fileSize = U32(header, offset + 12);
            if (fileSize == 0) continue;
            if (string.IsNullOrWhiteSpace(name) || !names.Add(name))
                throw new InvalidDataException("ExeFS contains an empty or duplicate entry name.");
            ValidateRange(fileOffset, fileSize, dataSize, $"ExeFS entry '{name}'");
            entries.Add(new(name, fileOffset, fileSize,
                partition.Offset + exefs + HeaderSize + fileOffset));
        }
        return entries;
    }

    private static CodeSetInfo ParseCodeSet(ReadOnlySpan<byte> data)
    {
        var name = Encoding.ASCII.GetString(data[..8]).TrimEnd('\0');
        return new(name, (data[0x0D] & 1) != 0, U16(data, 0x0E),
            U32(data, 0x10), U32(data, 0x14), U32(data, 0x18),
            U32(data, 0x20), U32(data, 0x24), U32(data, 0x28),
            U32(data, 0x30), U32(data, 0x34), U32(data, 0x38),
            U32(data, 0x3C), U32(data, 0x1C));
    }

    private static FileStream Open(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
        1024 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);

    private static async Task<byte[]> ReadAtAsync(FileStream stream, ulong offset, int count,
        CancellationToken cancellationToken)
    {
        if (offset > long.MaxValue) throw new InvalidDataException("File offset exceeds the host file API.");
        ValidateRange(offset, (ulong)count, (ulong)stream.Length, "File read");
        var data = new byte[count];
        stream.Position = (long)offset;
        await stream.ReadExactlyAsync(data, cancellationToken);
        return data;
    }

    private static void RequireMagic(ReadOnlySpan<byte> data, int offset, string magic, string message)
    {
        if (!data.Slice(offset, magic.Length).SequenceEqual(Encoding.ASCII.GetBytes(magic)))
            throw new InvalidDataException(message);
    }

    private static void ValidateOptionalRegion(ulong offset, ulong size, ulong containerSize, string label)
    {
        if (size == 0) return;
        ValidateRange(offset, size, containerSize, label);
    }

    private static void ValidateRange(ulong offset, ulong size, ulong containerSize, string label)
    {
        if (offset > containerSize || size > containerSize - offset)
            throw new InvalidDataException($"{label} is outside its container.");
    }

    private static ushort U16(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));

    private static uint U32(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));

    private static ulong U64(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset, 8));
}
