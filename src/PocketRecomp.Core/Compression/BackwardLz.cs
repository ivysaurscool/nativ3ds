using System.Buffers.Binary;

namespace PocketRecomp.Core.Compression;

/// <summary>Bounds-checked decoder for the backwards LZ stream used by compressed 3DS .code files.</summary>
public static class BackwardLz
{
    public const int MaxOutputSize = 256 * 1024 * 1024;

    public static byte[] Decompress(ReadOnlySpan<byte> compressed)
    {
        if (compressed.Length < 8) throw new InvalidDataException("Backward-LZ stream is shorter than its footer.");
        var footer = compressed[^8..];
        var topAndBottom = BinaryPrimitives.ReadUInt32LittleEndian(footer);
        var encodedSize = checked((int)(topAndBottom & 0x00FF_FFFFu));
        var footerSize = checked((int)(topAndBottom >> 24));
        var additionalSize = BinaryPrimitives.ReadUInt32LittleEndian(footer[4..]);
        var outputSize64 = (ulong)compressed.Length + additionalSize;

        if (encodedSize <= 0 || encodedSize > compressed.Length)
            throw new InvalidDataException("Backward-LZ encoded size is outside the input.");
        if (footerSize < 8 || footerSize > encodedSize)
            throw new InvalidDataException("Backward-LZ footer size is invalid.");
        if (outputSize64 > MaxOutputSize)
            throw new InvalidDataException("Backward-LZ output exceeds the 256 MiB safety limit.");

        var output = new byte[(int)outputSize64];
        compressed.CopyTo(output);
        var source = compressed.Length - footerSize;
        var sourceStop = compressed.Length - encodedSize;
        var destination = output.Length;

        while (source > sourceStop && destination > sourceStop)
        {
            var flags = output[--source];
            for (var mask = 0x80; mask != 0 && source > sourceStop && destination > sourceStop; mask >>= 1)
            {
                if ((flags & mask) == 0)
                {
                    if (source <= sourceStop) throw new InvalidDataException("Backward-LZ literal crosses its encoded boundary.");
                    output[--destination] = output[--source];
                    continue;
                }

                if (source - 2 < sourceStop) throw new InvalidDataException("Backward-LZ match crosses its encoded boundary.");
                source -= 2;
                var descriptor = output[source] | output[source + 1] << 8;
                var length = (descriptor >> 12) + 3;
                var displacement = (descriptor & 0x0FFF) + 2;
                for (var index = 0; index < length; index++)
                {
                    if (destination <= sourceStop) throw new InvalidDataException("Backward-LZ match overruns its output.");
                    destination--;
                    var copyFrom = destination + displacement + 1;
                    if ((uint)copyFrom >= (uint)output.Length)
                        throw new InvalidDataException("Backward-LZ match references data outside its output.");
                    output[destination] = output[copyFrom];
                }
            }
        }

        if (destination != sourceStop)
            throw new InvalidDataException($"Backward-LZ stream ended at output offset 0x{destination:X}, expected 0x{sourceStop:X}.");
        return output;
    }
}
