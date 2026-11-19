using System.Buffers.Binary;

namespace PocketRecomp.Core.Executables;

public enum ArmEntryMode { Arm, Thumb }

public sealed record Elf32Segment(uint VirtualAddress, uint MemorySize, uint FileSize,
    bool Readable, bool Writable, bool Executable, byte[] Data);

public sealed record Elf32Image(uint EntryPoint, ArmEntryMode EntryMode, uint Flags,
    IReadOnlyList<Elf32Segment> LoadSegments);

/// <summary>Bounds-checked ELF32 little-endian ARM loader for authorized homebrew executables.</summary>
public static class Elf32Reader
{
    private const ushort ArmMachine = 40;
    private const uint LoadProgramHeader = 1;
    private const uint MaxSegmentSize = 256 * 1024 * 1024;

    public static Elf32Image Read(ReadOnlySpan<byte> file)
    {
        if (file.Length < 52) throw new InvalidDataException("ELF file is shorter than the ELF32 header.");
        if (!file[..4].SequenceEqual(new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F' }))
            throw new InvalidDataException("ELF magic is missing.");
        if (file[4] != 1) throw new InvalidDataException("Only ELF32 files are supported.");
        if (file[5] != 1) throw new InvalidDataException("Only little-endian ELF files are supported.");
        if (file[6] != 1) throw new InvalidDataException("Unsupported ELF identification version.");
        if (U16(file, 18) != ArmMachine) throw new InvalidDataException("ELF machine must be ARM (EM_ARM).");
        if (U32(file, 20) != 1) throw new InvalidDataException("Unsupported ELF version.");
        if (U16(file, 40) < 52) throw new InvalidDataException("ELF header size is invalid.");

        var rawEntry = U32(file, 24);
        var programOffset = U32(file, 28);
        var flags = U32(file, 36);
        var programEntrySize = U16(file, 42);
        var programCount = U16(file, 44);
        if (programCount > 0 && programEntrySize < 32) throw new InvalidDataException("ELF program-header size is invalid.");
        var tableEnd = (ulong)programOffset + (ulong)programEntrySize * programCount;
        if (tableEnd > (ulong)file.Length) throw new InvalidDataException("ELF program-header table is outside the file.");

        var segments = new List<Elf32Segment>();
        for (var index = 0; index < programCount; index++)
        {
            var offset = checked((int)(programOffset + (uint)(index * programEntrySize)));
            if (U32(file, offset) != LoadProgramHeader) continue;
            var fileOffset = U32(file, offset + 4);
            var virtualAddress = U32(file, offset + 8);
            var fileSize = U32(file, offset + 16);
            var memorySize = U32(file, offset + 20);
            var segmentFlags = U32(file, offset + 24);
            if (fileSize > memorySize) throw new InvalidDataException($"ELF segment {index} file size exceeds memory size.");
            if (memorySize > MaxSegmentSize) throw new InvalidDataException($"ELF segment {index} exceeds the 256 MiB safety limit.");
            if ((ulong)fileOffset + fileSize > (ulong)file.Length)
                throw new InvalidDataException($"ELF segment {index} data is outside the file.");
            if ((ulong)virtualAddress + memorySize > uint.MaxValue + 1UL)
                throw new InvalidDataException($"ELF segment {index} wraps the 32-bit guest address space.");

            var data = new byte[checked((int)memorySize)];
            file.Slice(checked((int)fileOffset), checked((int)fileSize)).CopyTo(data);
            segments.Add(new(virtualAddress, memorySize, fileSize,
                Readable: (segmentFlags & 4) != 0,
                Writable: (segmentFlags & 2) != 0,
                Executable: (segmentFlags & 1) != 0,
                Data: data));
        }

        if (segments.Count == 0) throw new InvalidDataException("ELF has no loadable segments.");
        var entryMode = (rawEntry & 1) != 0 ? ArmEntryMode.Thumb : ArmEntryMode.Arm;
        var entryPoint = rawEntry & ~1u;
        var executableSegment = segments.Any(segment => segment.Executable &&
            entryPoint >= segment.VirtualAddress && (ulong)entryPoint < (ulong)segment.VirtualAddress + segment.MemorySize);
        if (!executableSegment) throw new InvalidDataException("ELF entry point is not inside an executable load segment.");
        return new(entryPoint, entryMode, flags, segments);
    }

    public static async Task<Elf32Image> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        return Read(bytes);
    }

    private static ushort U16(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));

    private static uint U32(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
}
