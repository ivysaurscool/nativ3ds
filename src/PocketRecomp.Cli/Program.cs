using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using PocketRecomp.Core.Arm;
using PocketRecomp.Core.Analysis;
using PocketRecomp.Core.Codegen;
using PocketRecomp.Core.Display;
using PocketRecomp.Core.Ir;
using PocketRecomp.Core.Runtime;
using PocketRecomp.Core.Projects;
using PocketRecomp.Core.Thumb;
using PocketRecomp.Core.Containers;
using PocketRecomp.Core.Executables;
using PocketRecomp.Core.Build;

return await PocketCli.RunAsync(args);

internal static class PocketCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            {
                PrintHelp();
                return 0;
            }

            return args[0].ToLowerInvariant() switch
            {
                "inspect" => await InspectAsync(args[1..]),
                "inspect-thumb" => await InspectThumbAsync(args[1..]),
                "inspect-container" => await InspectContainerAsync(args[1..]),
                "inspect-executable" => await InspectExecutableAsync(args[1..]),
                "analyze-cartridge" => await AnalyzeCartridgeAsync(args[1..]),
                "build" => await BuildAsync(args[1..]),
                "inspect-elf" => await InspectElfAsync(args[1..]),
                "recompile" => await RecompileAsync(args[1..]),
                "analyze-project" => await AnalyzeProjectAsync(args[1..]),
                "layout" => Layout(args[1..]),
                "demo" => await DemoAsync(args[1..]),
                _ => throw new ArgumentException($"Unknown command '{args[0]}'. Run with --help.")
            };
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or JsonException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }
    }

    private static async Task<int> InspectAsync(string[] args)
    {
        if (args.Length < 1) throw new ArgumentException("inspect requires a raw, decrypted ARM code file.");
        var code = await File.ReadAllBytesAsync(args[0]);
        var baseAddress = GetHexOption(args, "--base", 0x0010_0000u);
        var offset = checked((int)GetHexOption(args, "--offset", 0));
        var count = checked((int)GetUIntOption(args, "--count", 32));
        if ((offset & 3) != 0 || offset < 0 || offset > code.Length) throw new ArgumentException("--offset must be aligned and within the file.");

        Console.WriteLine("address    word        cond op                  operands");
        for (var cursor = offset; cursor + 4 <= code.Length && cursor < offset + count * 4; cursor += 4)
        {
            var word = BinaryPrimitives.ReadUInt32LittleEndian(code.AsSpan(cursor, 4));
            var i = ArmDecoder.Decode(word, baseAddress + (uint)cursor);
            Console.WriteLine($"{i.Address:X8}   {word:X8}    {i.Condition,-4} {i.Op,-19} {FormatOperands(i)}");
        }
        return 0;
    }

    private static async Task<int> RecompileAsync(string[] args)
    {
        if (args.Length < 1) throw new ArgumentException("recompile requires a raw, decrypted ARM code file.");
        var code = await File.ReadAllBytesAsync(args[0]);
        var baseAddress = GetHexOption(args, "--base", 0x0010_0000u);
        var offset = checked((int)GetHexOption(args, "--offset", 0));
        var output = GetStringOption(args, "--out") ?? Path.ChangeExtension(args[0], ".recompiled.cpp");
        var block = ArmToIrTranslator.TranslateBlock(code, baseAddress, offset);
        await File.WriteAllTextAsync(output, CppEmitter.Emit(block));
        Console.WriteLine($"Translated block 0x{block.Address:X8}: {block.Instructions.Count} IR operations -> {output}");
        return 0;
    }

    private static async Task<int> InspectThumbAsync(string[] args)
    {
        if (args.Length < 1) throw new ArgumentException("inspect-thumb requires a raw, authorized Thumb code file.");
        var code = await File.ReadAllBytesAsync(args[0]);
        var baseAddress = GetHexOption(args, "--base", 0x0010_0000u);
        var offset = checked((int)GetHexOption(args, "--offset", 0));
        var count = checked((int)GetUIntOption(args, "--count", 32));
        if ((offset & 1) != 0 || offset < 0 || offset > code.Length)
            throw new ArgumentException("--offset must be halfword aligned and within the file.");

        Console.WriteLine("address    encoding  size op                          operands");
        for (var cursor = offset; cursor + 2 <= code.Length && count-- > 0;)
        {
            var instruction = ThumbDecoder.Decode(code.AsSpan(cursor), baseAddress + (uint)cursor);
            Console.WriteLine($"{instruction.Address:X8}   {instruction.Encoding:X8}  {instruction.Size,2}   {instruction.Op,-27} {FormatThumbOperands(instruction)}");
            cursor += instruction.Size;
        }
        return 0;
    }

    private static async Task<int> InspectContainerAsync(string[] args)
    {
        if (args.Length < 1) throw new ArgumentException("inspect-container requires a local .3ds or NCCH file.");
        var info = await ThreeDsContainer.InspectAsync(args[0]);
        Console.WriteLine($"File:          {info.FilePath}");
        Console.WriteLine($"Type:          {info.Kind}");
        Console.WriteLine($"Header magic:  {info.Magic}");
        Console.WriteLine($"File size:     {info.FileSize} bytes");
        if (info.DeclaredSize is { } declared)
            Console.WriteLine($"Declared size: {declared} bytes ({(info.SizeMatches ? "present" : "truncated")})");
        Console.WriteLine("Decryption:    not attempted; PocketRecomp never accepts or stores console/game keys");
        return info.Kind == ThreeDsContainerKind.Unknown || !info.SizeMatches ? 3 : 0;
    }

    private static async Task<int> InspectExecutableAsync(string[] args)
    {
        if (args.Length < 1) throw new ArgumentException("inspect-executable requires a local, authorized .3ds file.");
        var executable = await CartridgeReader.LoadExecutableAsync(args[0]);
        var header = executable.Partition.Header;
        var code = executable.CodeSet;
        Console.WriteLine($"Title:          {code.Name}");
        Console.WriteLine($"Product code:   {header.ProductCode}");
        Console.WriteLine($"Program ID:     0x{header.ProgramId:X16}");
        Console.WriteLine($"Content version:{header.ContentVersion,8}");
        Console.WriteLine($"Partition:      {executable.Partition.Index} at 0x{executable.Partition.Offset:X}");
        Console.WriteLine($"Stored .code:   0x{executable.ExeFsEntries.Single(entry => entry.Name == ".code").Size:X} bytes");
        Console.WriteLine($"Mapped .code:   0x{executable.Code.Length:X} bytes ({(code.Compressed ? "decompressed" : "uncompressed")})");
        Console.WriteLine($"Stored SHA-256: {executable.StoredCodeSha256}");
        Console.WriteLine($"Mapped SHA-256: {executable.MappedCodeSha256}");
        Console.WriteLine($"Text:           0x{code.TextAddress:X8}, size=0x{code.TextSize:X}, pages=0x{code.TextPages:X}");
        Console.WriteLine($"RO:             0x{code.RoAddress:X8}, size=0x{code.RoSize:X}, pages=0x{code.RoPages:X}");
        Console.WriteLine($"Data:           0x{code.DataAddress:X8}, size=0x{code.DataSize:X}, pages=0x{code.DataPages:X}");
        Console.WriteLine($"BSS/stack:      0x{code.BssSize:X} / 0x{code.StackSize:X}");
        Console.WriteLine($"ExeFS:          {string.Join(", ", executable.ExeFsEntries.Select(entry => $"{entry.Name}=0x{entry.Size:X}"))}");
        return 0;
    }

    private static async Task<int> AnalyzeCartridgeAsync(string[] args)
    {
        if (args.Length < 1) throw new ArgumentException("analyze-cartridge requires a local, authorized .3ds file.");
        var executable = await CartridgeReader.LoadExecutableAsync(args[0]);
        var codeSet = executable.CodeSet;
        var textBytes = checked((int)(codeSet.TextPages * 0x1000u));
        var graph = ControlFlowDiscoverer.Discover(executable.Code.AsSpan(0, textBytes),
            codeSet.TextAddress, [codeSet.TextAddress]);
        var unknown = graph.Blocks.Values
            .Where(block => block.EndsWithUnsupported)
            .Select(block => block.Instructions[^1])
            .OrderBy(instruction => instruction.Address)
            .ToArray();
        Console.WriteLine($"Title:          {codeSet.Name} ({executable.Partition.Header.ProductCode})");
        Console.WriteLine($"Entry point:    0x{codeSet.TextAddress:X8} (ARM state)");
        Console.WriteLine($"Reachable:      {graph.Blocks.Count} blocks, {graph.InstructionCount} instructions");
        Console.WriteLine($"Unsupported:    {unknown.Length} reachable block endings");
        Console.WriteLine($"External calls: {graph.UnresolvedTargets.Count}");
        foreach (var instruction in unknown.Take(32))
            Console.WriteLine($"  unsupported 0x{instruction.Word:X8} at 0x{instruction.Address:X8}");
        foreach (var target in graph.UnresolvedTargets.Take(32))
            Console.WriteLine($"  external target 0x{target:X8}");
        return unknown.Length == 0 ? 0 : 3;
    }

    private static async Task<int> BuildAsync(string[] args)
    {
        if (args.Length < 1) throw new ArgumentException("build requires a local, authorized .3ds file.");
        var output = GetStringOption(args, "--output") ?? "build-output";
        var manifestDirectory = Path.Combine(AppContext.BaseDirectory, "titles");
        var manifests = await NativeBuildPipeline.LoadManifestsAsync(manifestDirectory);
        Console.WriteLine("Hashing and loading cartridge...");
        var result = await NativeBuildPipeline.PrepareAsync(args[0], output, manifests);
        Console.WriteLine($"Title:       {result.Report.DisplayName}");
        Console.WriteLine($"Status:      {result.Report.Status}");
        Console.WriteLine($"Reachable:   {result.Report.ReachableBlocks} blocks, {result.Report.ReachableInstructions} instructions");
        Console.WriteLine($"Unsupported: {result.Report.UnsupportedEncodings.Count} unique encodings");
        Console.WriteLine($"Report:      {result.ReportPath}");
        Console.WriteLine($"Next:        {result.Report.NextRequiredStage}");
        if (!result.Report.NativeExecutableProduced)
            Console.WriteLine("No executable was produced yet; the report records the remaining native-build blockers.");
        return result.Report.NativeExecutableProduced ? 0 : 4;
    }

    private static async Task<int> InspectElfAsync(string[] args)
    {
        if (args.Length < 1) throw new ArgumentException("inspect-elf requires an authorized ARM ELF file.");
        var image = await Elf32Reader.ReadAsync(args[0]);
        Console.WriteLine($"Entry point: 0x{image.EntryPoint:X8} ({image.EntryMode})");
        Console.WriteLine($"ARM flags:   0x{image.Flags:X8}");
        Console.WriteLine($"Segments:    {image.LoadSegments.Count}");
        foreach (var segment in image.LoadSegments)
        {
            var permissions = $"{(segment.Readable ? 'R' : '-')}{(segment.Writable ? 'W' : '-')}{(segment.Executable ? 'X' : '-')}";
            Console.WriteLine($"  0x{segment.VirtualAddress:X8}-0x{(ulong)segment.VirtualAddress + segment.MemorySize:X8} {permissions} file={segment.FileSize} memory={segment.MemorySize}");
        }
        return 0;
    }

    private static async Task<int> AnalyzeProjectAsync(string[] args)
    {
        if (args.Length < 1) throw new ArgumentException("analyze-project requires a project manifest JSON file.");
        var project = await ProjectLoader.LoadAsync(args[0]);
        var strict = args.Contains("--strict", StringComparer.OrdinalIgnoreCase);
        var problems = false;
        Console.WriteLine($"Project: {project.Name}");
        foreach (var segment in project.Segments)
        {
            var graph = ControlFlowDiscoverer.Discover(segment.Code, segment.GuestAddress, segment.EntryPoints);
            var supported = graph.Blocks.Values.Sum(block => block.Instructions.Count(i => i.Op != ArmOp.Unknown));
            var total = graph.InstructionCount;
            Console.WriteLine($"Segment '{segment.Name}': {segment.Code.Length} bytes at 0x{segment.GuestAddress:X8}");
            Console.WriteLine($"  SHA-256:     {segment.Sha256}");
            Console.WriteLine($"  Reachable:   {graph.Blocks.Count} blocks, {total} instructions");
            Console.WriteLine($"  Decoded:     {supported}/{total} reachable instructions");
            Console.WriteLine($"  Unsupported: {graph.UnsupportedBlockCount} blocks");
            if (graph.UnresolvedTargets.Count > 0)
                Console.WriteLine($"  External:    {string.Join(", ", graph.UnresolvedTargets.Select(value => $"0x{value:X8}"))}");
            problems |= graph.UnsupportedBlockCount > 0 || graph.UnresolvedTargets.Count > 0;
        }
        return strict && problems ? 3 : 0;
    }

    private static int Layout(string[] args)
    {
        var width = checked((int)GetUIntOption(args, "--width", 1920));
        var height = checked((int)GetUIntOption(args, "--height", 1080));
        var modeText = GetStringOption(args, "--mode") ?? "top-only";
        var mode = modeText.ToLowerInvariant() switch
        {
            "top-only" => ScreenLayout.TopOnly,
            "stacked" => ScreenLayout.Stacked,
            "side-by-side" => ScreenLayout.SideBySide,
            _ => throw new ArgumentException("--mode must be top-only, stacked, or side-by-side.")
        };
        var result = DisplayLayout.Calculate(width, height, mode);
        Console.WriteLine($"Output: {width}x{height}  Mode: {mode}");
        Console.WriteLine($"Top:   x={result.Top.X} y={result.Top.Y} w={result.Top.Width} h={result.Top.Height}");
        if (result.Bottom is { } bottom)
            Console.WriteLine($"Bottom: x={bottom.X} y={bottom.Y} w={bottom.Width} h={bottom.Height}");
        return 0;
    }

    private static async Task<int> DemoAsync(string[] args)
    {
        var outputDirectory = GetStringOption(args, "--out") ?? "demo-output";
        Directory.CreateDirectory(outputDirectory);
        // mov r0,#42; add r1,r0,#8; cmp r1,#50; bx lr
        uint[] words = [0xE3A0002A, 0xE2801008, 0xE3510032, 0xE12FFF1E];
        var bytes = new byte[words.Length * 4];
        for (var n = 0; n < words.Length; n++) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(n * 4), words[n]);
        var binaryPath = Path.Combine(outputDirectory, "sample-arm.bin");
        var cppPath = Path.Combine(outputDirectory, "sample-arm.recompiled.cpp");
        var projectPath = Path.Combine(outputDirectory, "project.json");
        await File.WriteAllBytesAsync(binaryPath, bytes);
        var block = ArmToIrTranslator.TranslateBlock(bytes, 0x0010_0000u);
        await File.WriteAllTextAsync(cppPath, CppEmitter.Emit(block));
        var demoManifest = new
        {
            formatVersion = 1,
            name = "PocketRecomp ARM demo",
            segments = new[]
            {
                new { name = "text", path = "sample-arm.bin", guestAddress = "0x00100000", mode = "arm", entryPoints = new[] { "0x00100000" } }
            }
        };
        await File.WriteAllTextAsync(projectPath, JsonSerializer.Serialize(demoManifest, new JsonSerializerOptions { WriteIndented = true }));
        var layout = DisplayLayout.Calculate();
        var timing = new FrameTiming().Validate();
        Console.WriteLine($"Demo ARM block: {binaryPath}");
        Console.WriteLine($"Generated C++:   {cppPath}");
        Console.WriteLine($"Project file:    {projectPath}");
        Console.WriteLine($"1080p viewport:  {layout.Top.Width}x{layout.Top.Height} at ({layout.Top.X},{layout.Top.Y})");
        Console.WriteLine($"Timing:           {timing.GuestFps:F0} Hz simulation, {timing.PresentationFps:F0} Hz presentation");
        return 0;
    }

    private static string FormatOperands(ArmInstruction i) => i.Op switch
    {
        ArmOp.MoveImmediate => $"r{i.Rd}, #0x{i.Immediate:X}",
        ArmOp.MoveRegister => FormatShiftedRegister(i, $"r{i.Rd}, r{i.Rm}"),
        ArmOp.MoveNotImmediate => $"r{i.Rd}, #0x{i.Immediate:X}",
        ArmOp.MoveNotRegister => FormatShiftedRegister(i, $"r{i.Rd}, r{i.Rm}"),
        ArmOp.AndImmediate or ArmOp.OrImmediate or ArmOp.BitClearImmediate or
            ArmOp.AddImmediate or ArmOp.SubtractImmediate => $"r{i.Rd}, r{i.Rn}, #0x{i.Immediate:X}",
        ArmOp.AndRegister or ArmOp.OrRegister or ArmOp.BitClearRegister or
            ArmOp.AddRegister or ArmOp.SubtractRegister => FormatShiftedRegister(i, $"r{i.Rd}, r{i.Rn}, r{i.Rm}"),
        ArmOp.CompareImmediate => $"r{i.Rn}, #0x{i.Immediate:X}",
        ArmOp.CompareRegister => FormatShiftedRegister(i, $"r{i.Rn}, r{i.Rm}"),
        ArmOp.TestImmediate => $"r{i.Rn}, #0x{i.Immediate:X}",
        ArmOp.TestRegister => FormatShiftedRegister(i, $"r{i.Rn}, r{i.Rm}"),
        ArmOp.LoadImmediate or ArmOp.StoreImmediate or ArmOp.LoadByteImmediate or ArmOp.StoreByteImmediate
            => $"r{i.Rd}, [r{i.Rn}, #0x{i.Immediate:X}]",
        ArmOp.LoadHalfwordImmediate or ArmOp.StoreHalfwordImmediate or
            ArmOp.LoadSignedByteImmediate or ArmOp.LoadSignedHalfwordImmediate
            => $"r{i.Rd}, [r{i.Rn}, #0x{i.Immediate:X}]",
        ArmOp.LoadMultiple or ArmOp.StoreMultiple => $"r{i.Rn}{(i.WriteBack ? "!" : "")}, register-list=0x{i.RegisterList:X4}",
        ArmOp.Push or ArmOp.Pop => $"register-list=0x{i.RegisterList:X4}",
        ArmOp.Branch or ArmOp.BranchLink => $"0x{i.Target:X8}",
        ArmOp.BranchExchange or ArmOp.BranchLinkExchange => $"r{i.Rm}",
        ArmOp.SoftwareInterrupt => $"#0x{i.Immediate:X}",
        ArmOp.ReadThreadPointer => $"r{i.Rd}",
        _ => ""
    };

    private static string FormatShiftedRegister(ArmInstruction instruction, string operands)
    {
        if (instruction.ShiftType == ArmShiftType.Lsl && instruction.ShiftAmount == 0) return operands;
        var amount = instruction.ShiftAmount == 0 && instruction.ShiftType is ArmShiftType.Lsr or ArmShiftType.Asr
            ? 32 : instruction.ShiftAmount;
        var suffix = instruction.ShiftType == ArmShiftType.Ror && instruction.ShiftAmount == 0
            ? "RRX"
            : $"{instruction.ShiftType.ToString().ToUpperInvariant()} #{amount}";
        return $"{operands}, {suffix}";
    }

    private static string FormatThumbOperands(ThumbInstruction i) => i.Op switch
    {
        ThumbOp.MoveImmediate or ThumbOp.CompareImmediate or ThumbOp.AddImmediate8 or ThumbOp.SubtractImmediate8
            => $"r{i.Rd}, #0x{i.Immediate:X}",
        ThumbOp.AddImmediate3 or ThumbOp.SubtractImmediate3
            => $"r{i.Rd}, r{i.Rn}, #0x{i.Immediate:X}",
        ThumbOp.AddRegister or ThumbOp.SubtractRegister => $"r{i.Rd}, r{i.Rn}, r{i.Rm}",
        ThumbOp.LogicalShiftLeftImmediate or ThumbOp.LogicalShiftRightImmediate or ThumbOp.ArithmeticShiftRightImmediate
            => $"r{i.Rd}, r{i.Rm}, #{i.Immediate}",
        ThumbOp.LoadLiteral => $"r{i.Rd}, [pc] ; 0x{i.Target:X8}",
        ThumbOp.LoadWordImmediate or ThumbOp.StoreWordImmediate => $"r{i.Rd}, [r{i.Rn}, #0x{i.Immediate:X}]",
        ThumbOp.Branch or ThumbOp.BranchLink or ThumbOp.BranchConditional => $"0x{i.Target:X8}",
        ThumbOp.BranchExchange => $"r{i.Rm}",
        ThumbOp.Push or ThumbOp.Pop => $"register-list=0x{i.RegisterList:X4}",
        ThumbOp.SupervisorCall => $"#0x{i.Immediate:X}",
        _ => ""
    };

    private static uint GetHexOption(string[] args, string name, uint fallback)
    {
        var value = GetStringOption(args, name);
        if (value is null) return fallback;
        value = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
        return uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed)
            ? parsed : throw new ArgumentException($"{name} expects a hexadecimal integer.");
    }

    private static uint GetUIntOption(string[] args, string name, uint fallback)
    {
        var value = GetStringOption(args, name);
        return value is null ? fallback : uint.TryParse(value, out var parsed)
            ? parsed : throw new ArgumentException($"{name} expects an integer.");
    }

    private static string? GetStringOption(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        if (index < 0) return null;
        if (index + 1 >= args.Length) throw new ArgumentException($"{name} requires a value.");
        return args[index + 1];
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            nativ3ds milestone 0.3 - experimental Nintendo 3DS static recompiler

            Usage:
              nativ3ds inspect <decrypted-code.bin> [--base HEX] [--offset HEX] [--count N]
              nativ3ds inspect-thumb <code.bin> [--base HEX] [--offset HEX] [--count N]
              nativ3ds inspect-container <game.3ds|content.cxi>
              nativ3ds inspect-executable <game.3ds>
              nativ3ds analyze-cartridge <game.3ds>
              nativ3ds build <game.3ds> [--output DIRECTORY]
              nativ3ds inspect-elf <homebrew.elf>
              nativ3ds recompile <decrypted-code.bin> [--base HEX] [--offset HEX] [--out FILE]
              nativ3ds analyze-project <project.json> [--strict]
              nativ3ds layout [--width 1920] [--height 1080] [--mode top-only|stacked|side-by-side]
              nativ3ds demo [--out DIRECTORY]

            This tool accepts only raw code you are legally authorized to use. It does not decrypt,
            extract, download, or distribute games, keys, firmware, system files, or Nintendo assets.
            """);
    }
}
