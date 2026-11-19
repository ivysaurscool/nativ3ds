using System.Buffers.Binary;
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

var tests = new (string Name, Action Run)[]
{
    ("decodes MOV immediate", DecodeMove),
    ("decodes forward branch", DecodeBranch),
    ("decodes BX LR", DecodeBx),
    ("rejects unsupported flag-setting arithmetic", RejectFlagSetting),
    ("rejects byte load as word load", RejectByteLoad),
    ("translates and terminates block", TranslateBlock),
    ("emits C++", EmitCpp),
    ("wraps conditional instruction", ConditionalCpp),
    ("discovers direct call graph", DiscoverGraph),
    ("loads safe project manifest", LoadProject),
    ("decodes Thumb immediate operation", DecodeThumbImmediate),
    ("decodes Thumb BX LR", DecodeThumbBx),
    ("decodes Thumb conditional branch", DecodeThumbConditionalBranch),
    ("decodes Thumb long branch with link", DecodeThumbBl),
    ("executes translated arithmetic", ExecuteArithmetic),
    ("honors ARM condition guards", ExecuteConditional),
    ("executes guest memory operations", ExecuteMemory),
    ("models ARM branch with link", ExecuteCall),
    ("recognizes 3DS cartridge container", InspectThreeDsContainer),
    ("loads ARM ELF32 executable", LoadArmElf),
    ("fits top screen at 1080p", TopLayout),
    ("fits stacked screens", StackedLayout),
    ("validates high-FPS timing", Timing)
};

var failures = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception ex) { failures++; Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}"); }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} tests passed");
return failures == 0 ? 0 : 1;

static void DecodeMove()
{
    var i = ArmDecoder.Decode(0xE3A0002Au, 0x100000);
    Equal(ArmOp.MoveImmediate, i.Op); Equal(0, i.Rd); Equal(42u, i.Immediate);
}

static void DecodeBranch()
{
    var i = ArmDecoder.Decode(0xEA000002u, 0x1000);
    Equal(ArmOp.Branch, i.Op); Equal(0x1010u, i.Target);
}

static void DecodeBx()
{
    var i = ArmDecoder.Decode(0xE12FFF1Eu, 0x1000);
    Equal(ArmOp.BranchExchange, i.Op); Equal(14, i.Rm);
}

static void RejectFlagSetting()
{
    // MOVS r0,#42 requires CPSR flag writes, which are not in milestone 0.1 IR.
    Equal(ArmOp.Unknown, ArmDecoder.Decode(0xE3B0002Au, 0x1000).Op);
}

static void RejectByteLoad()
{
    // LDRB r0,[r1] must not be silently lowered as LDR.
    Equal(ArmOp.Unknown, ArmDecoder.Decode(0xE5D10000u, 0x1000).Op);
}

static void TranslateBlock()
{
    var code = Words(0xE3A0002Au, 0xE2801008u, 0xE12FFF1Eu, 0xE3A02001u);
    var block = ArmToIrTranslator.TranslateBlock(code, 0x100000);
    Equal(3, block.Instructions.Count); Equal(IrOp.JumpRegister, block.Instructions[^1].Op);
}

static void EmitCpp()
{
    var block = ArmToIrTranslator.TranslateBlock(Words(0xE3A0002Au, 0xE12FFF1Eu), 0x100000);
    var cpp = CppEmitter.Emit(block);
    True(cpp.Contains("cpu.r[0] = 0x2Au;", StringComparison.Ordinal));
    True(cpp.Contains("block_00100000", StringComparison.Ordinal));
}

static void ConditionalCpp()
{
    // MOVEQ r0,#1; BX LR
    var block = ArmToIrTranslator.TranslateBlock(Words(0x03A00001u, 0xE12FFF1Eu), 0x100000);
    var cpp = CppEmitter.Emit(block);
    True(cpp.Contains("if (cpu.condition(0u)) {", StringComparison.Ordinal));
    True(!cpp.Contains("if (!cpu.condition", StringComparison.Ordinal));
}

static void DiscoverGraph()
{
    // 1000: mov r0,#1; 1004: bl 1010; 1008: bx lr; 100c: padding; 1010: mov r1,#2; 1014: bx lr
    var code = Words(0xE3A00001u, 0xEB000001u, 0xE12FFF1Eu, 0u, 0xE3A01002u, 0xE12FFF1Eu);
    var graph = ControlFlowDiscoverer.Discover(code, 0x1000, [0x1000u]);
    True(graph.Blocks.ContainsKey(0x1000));
    True(graph.Blocks.ContainsKey(0x1008));
    True(graph.Blocks.ContainsKey(0x1010));
    Equal(3, graph.Blocks.Count);
    Equal(0, graph.UnsupportedBlockCount);
}

static void LoadProject()
{
    var root = Path.Combine(Path.GetTempPath(), "PocketRecompTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var code = Words(0xE3A00001u, 0xE12FFF1Eu);
        File.WriteAllBytes(Path.Combine(root, "code.bin"), code);
        File.WriteAllText(Path.Combine(root, "project.json"), """
            {
              "formatVersion": 1,
              "name": "fixture",
              "segments": [{
                "name": "text", "path": "code.bin", "guestAddress": "0x00100000",
                "mode": "arm", "entryPoints": ["0x00100000"]
              }]
            }
            """);
        var project = ProjectLoader.LoadAsync(Path.Combine(root, "project.json")).GetAwaiter().GetResult();
        Equal("fixture", project.Name);
        Equal(1, project.Segments.Count);
        Equal(0x00100000u, project.Segments[0].GuestAddress);
        Equal(64, project.Segments[0].Sha256.Length);
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static void DecodeThumbImmediate()
{
    var instruction = ThumbDecoder.Decode16(0x202A, 0x1000); // MOVS r0,#42
    Equal(ThumbOp.MoveImmediate, instruction.Op);
    Equal(0, instruction.Rd);
    Equal(42u, instruction.Immediate);
    Equal(2, instruction.Size);
}

static void DecodeThumbBx()
{
    var instruction = ThumbDecoder.Decode16(0x4770, 0x1000); // BX LR
    Equal(ThumbOp.BranchExchange, instruction.Op);
    Equal(14, instruction.Rm);
}

static void DecodeThumbConditionalBranch()
{
    var instruction = ThumbDecoder.Decode16(0xD102, 0x1000); // BNE +4
    Equal(ThumbOp.BranchConditional, instruction.Op);
    Equal(ArmCondition.Ne, instruction.Condition);
    Equal(0x1008u, instruction.Target);
}

static void DecodeThumbBl()
{
    var bytes = new byte[4];
    BinaryPrimitives.WriteUInt16LittleEndian(bytes, 0xF000);
    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), 0xF802);
    var instruction = ThumbDecoder.Decode(bytes, 0x1000);
    Equal(ThumbOp.BranchLink, instruction.Op);
    Equal(4, instruction.Size);
    Equal(0x1008u, instruction.Target);
}

static void ExecuteArithmetic()
{
    var code = Words(0xE3A0002Au, 0xE2801008u, 0xE3510032u, 0xE12FFF1Eu);
    var block = ArmToIrTranslator.TranslateBlock(code, 0x100000);
    var cpu = new ReferenceCpu();
    cpu.Registers[14] = 0xCAFEBABEu;
    var result = IrInterpreter.Execute(block, cpu, new GuestMemory(0x200000, 4096));
    Equal(42u, cpu.Registers[0]);
    Equal(50u, cpu.Registers[1]);
    True(cpu.Zero && cpu.Carry && !cpu.Negative && !cpu.Overflow);
    Equal(HaltReason.JumpRegister, result.Reason);
    EqualNullableUInt(0xCAFEBABEu, result.Target);
}

static void ExecuteConditional()
{
    // MOVEQ r0,#1 is skipped because the initial Z flag is clear.
    var block = ArmToIrTranslator.TranslateBlock(Words(0x03A00001u, 0xE12FFF1Eu), 0x100000);
    var cpu = new ReferenceCpu();
    var result = IrInterpreter.Execute(block, cpu, new GuestMemory(0x200000, 64));
    Equal(0u, cpu.Registers[0]);
    Equal(HaltReason.JumpRegister, result.Reason);
}

static void ExecuteMemory()
{
    // STR r0,[r1,#4]; LDR r2,[r1,#4]; BX LR
    var block = ArmToIrTranslator.TranslateBlock(Words(0xE5810004u, 0xE5912004u, 0xE12FFF1Eu), 0x100000);
    var cpu = new ReferenceCpu();
    var memory = new GuestMemory(0x200000, 64);
    cpu.Registers[0] = 0x12345678u;
    cpu.Registers[1] = 0x200000u;
    IrInterpreter.Execute(block, cpu, memory);
    Equal(0x12345678u, memory.Load32(0x200004));
    Equal(0x12345678u, cpu.Registers[2]);
}

static void ExecuteCall()
{
    var block = ArmToIrTranslator.TranslateBlock(Words(0xEB000002u), 0x1000);
    var cpu = new ReferenceCpu();
    var result = IrInterpreter.Execute(block, cpu, new GuestMemory(0x2000, 64));
    Equal(HaltReason.Call, result.Reason);
    EqualNullableUInt(0x1010u, result.Target);
    Equal(0x1004u, cpu.Registers[14]);
}

static void InspectThreeDsContainer()
{
    var root = Path.Combine(Path.GetTempPath(), "PocketRecompTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var image = new byte[0x400];
        "NCSD"u8.CopyTo(image.AsSpan(0x100));
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x104), 2); // two 0x200-byte media units
        var path = Path.Combine(root, "fixture.3ds");
        File.WriteAllBytes(path, image);
        var info = ThreeDsContainer.InspectAsync(path).GetAwaiter().GetResult();
        Equal(ThreeDsContainerKind.NcsdCartridge, info.Kind);
        Equal(0x400L, info.FileSize);
        EqualNullableUlong(0x400UL, info.DeclaredSize);
        True(info.SizeMatches);
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static void LoadArmElf()
{
    var elf = new byte[0x104];
    elf[0] = 0x7F; elf[1] = (byte)'E'; elf[2] = (byte)'L'; elf[3] = (byte)'F';
    elf[4] = 1; elf[5] = 1; elf[6] = 1;
    BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(16), 2); // executable
    BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(18), 40); // ARM
    BinaryPrimitives.WriteUInt32LittleEndian(elf.AsSpan(20), 1);
    BinaryPrimitives.WriteUInt32LittleEndian(elf.AsSpan(24), 0x00100001); // Thumb entry
    BinaryPrimitives.WriteUInt32LittleEndian(elf.AsSpan(28), 52); // program headers
    BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(40), 52);
    BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(42), 32);
    BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(44), 1);
    BinaryPrimitives.WriteUInt32LittleEndian(elf.AsSpan(52), 1); // PT_LOAD
    BinaryPrimitives.WriteUInt32LittleEndian(elf.AsSpan(56), 0x100); // file offset
    BinaryPrimitives.WriteUInt32LittleEndian(elf.AsSpan(60), 0x00100000); // vaddr
    BinaryPrimitives.WriteUInt32LittleEndian(elf.AsSpan(68), 4); // file size
    BinaryPrimitives.WriteUInt32LittleEndian(elf.AsSpan(72), 8); // memory size including BSS
    BinaryPrimitives.WriteUInt32LittleEndian(elf.AsSpan(76), 5); // R-X
    elf[0x100] = 0x70; elf[0x101] = 0x47; // BX LR
    var image = Elf32Reader.Read(elf);
    Equal(0x00100000u, image.EntryPoint);
    Equal(ArmEntryMode.Thumb, image.EntryMode);
    Equal(1, image.LoadSegments.Count);
    Equal(8u, image.LoadSegments[0].MemorySize);
    True(image.LoadSegments[0].Executable && image.LoadSegments[0].Readable && !image.LoadSegments[0].Writable);
    Equal((byte)0, image.LoadSegments[0].Data[7]);
}

static void TopLayout()
{
    var layout = DisplayLayout.Calculate(1920, 1080, ScreenLayout.TopOnly);
    Equal(new Viewport(60, 0, 1800, 1080), layout.Top); EqualNullable(null, layout.Bottom);
}

static void StackedLayout()
{
    var layout = DisplayLayout.Calculate(1920, 1080, ScreenLayout.Stacked, 16);
    True(layout.Top.Width <= 1920 && layout.Top.Y >= 0);
    True(layout.Bottom is { Y: >= 0 } && layout.Bottom.Value.Y + layout.Bottom.Value.Height <= 1080);
}

static void Timing()
{
    var timing = new FrameTiming(60, 144).Validate();
    EqualDouble(2.4, timing.InterpolationFrames, 0.00001);
}

static byte[] Words(params uint[] words)
{
    var bytes = new byte[words.Length * 4];
    for (var i = 0; i < words.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), words[i]);
    return bytes;
}

static void Equal<T>(T expected, T actual) where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"expected {expected}, got {actual}");
}

static void EqualNullable(Viewport? expected, Viewport? actual)
{
    if (expected != actual) throw new Exception($"expected {expected}, got {actual}");
}

static void EqualDouble(double expected, double actual, double tolerance)
{
    if (Math.Abs(expected - actual) > tolerance) throw new Exception($"expected {expected}, got {actual}");
}

static void EqualNullableUInt(uint? expected, uint? actual)
{
    if (expected != actual) throw new Exception($"expected {expected}, got {actual}");
}

static void EqualNullableUlong(ulong? expected, ulong? actual)
{
    if (expected != actual) throw new Exception($"expected {expected}, got {actual}");
}

static void True(bool condition)
{
    if (!condition) throw new Exception("condition was false");
}
