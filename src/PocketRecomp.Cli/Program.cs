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
        ArmOp.AddImmediate or ArmOp.SubtractImmediate => $"r{i.Rd}, r{i.Rn}, #0x{i.Immediate:X}",
        ArmOp.CompareImmediate => $"r{i.Rn}, #0x{i.Immediate:X}",
        ArmOp.LoadImmediate or ArmOp.StoreImmediate => $"r{i.Rd}, [r{i.Rn}, #0x{i.Immediate:X}]",
        ArmOp.Branch or ArmOp.BranchLink => $"0x{i.Target:X8}",
        ArmOp.BranchExchange => $"r{i.Rm}",
        ArmOp.SoftwareInterrupt => $"#0x{i.Immediate:X}",
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
            PocketRecomp milestone 0.2 - experimental Nintendo 3DS static recompiler

            Usage:
              pocketrecomp inspect <decrypted-code.bin> [--base HEX] [--offset HEX] [--count N]
              pocketrecomp recompile <decrypted-code.bin> [--base HEX] [--offset HEX] [--out FILE]
              pocketrecomp analyze-project <project.json> [--strict]
              pocketrecomp layout [--width 1920] [--height 1080] [--mode top-only|stacked|side-by-side]
              pocketrecomp demo [--out DIRECTORY]

            This tool accepts only raw code you are legally authorized to use. It does not decrypt,
            extract, download, or distribute games, keys, firmware, system files, or Nintendo assets.
            """);
    }
}
