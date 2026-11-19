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
using PocketRecomp.Core.Compression;
using PocketRecomp.Core.Build;

var tests = new (string Name, Action Run)[]
{
    ("decodes MOV immediate", DecodeMove),
    ("decodes forward branch", DecodeBranch),
    ("decodes BX LR", DecodeBx),
    ("decodes ARM register compare", DecodeRegisterCompare),
    ("decodes ARM stack aliases", DecodeArmStack),
    ("decodes ARM multiple transfer", DecodeMultipleTransfer),
    ("decodes ARM logical immediates and NOP", DecodeLogicalImmediate),
    ("decodes ARM BLX register", DecodeBlxRegister),
    ("decodes flag-setting arithmetic", DecodeFlagSettingArithmetic),
    ("decodes byte memory operations", DecodeByteMemory),
    ("decodes signed memory and TLS operations", DecodeSignedMemoryAndTls),
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
    ("executes indexed memory writeback", ExecuteMemoryWriteback),
    ("executes byte and logical operations", ExecuteByteAndLogical),
    ("executes signed memory and TLS operations", ExecuteSignedMemoryAndTls),
    ("executes ARM shifted logical flags", ExecuteShiftedLogicalFlags),
    ("executes ARM arithmetic flags and tests", ExecuteArithmeticFlags),
    ("models ARM branch with link", ExecuteCall),
    ("executes ARM push and pop", ExecutePushPop),
    ("executes ARM multiple transfer", ExecuteMultipleTransfer),
    ("recognizes 3DS cartridge container", InspectThreeDsContainer),
    ("loads ARM ELF32 executable", LoadArmElf),
    ("decompresses backward LZ code", DecompressBackwardLz),
    ("loads executable from cartridge", LoadCartridgeExecutable),
    ("prepares native build report", PrepareNativeBuild),
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

static void DecodeRegisterCompare()
{
    var instruction = ArmDecoder.Decode(0xE1500001u, 0x1000); // CMP r0,r1
    Equal(ArmOp.CompareRegister, instruction.Op);
    Equal(0, instruction.Rn);
    Equal(1, instruction.Rm);
}

static void DecodeArmStack()
{
    var push = ArmDecoder.Decode(0xE92D4070u, 0x1000); // PUSH {r4-r6,lr}
    Equal(ArmOp.Push, push.Op);
    Equal((ushort)0x4070, push.RegisterList);
    var pop = ArmDecoder.Decode(0xE8BD8070u, 0x1004); // POP {r4-r6,pc}
    Equal(ArmOp.Pop, pop.Op);
    True(pop.TerminatesBlock);
}

static void DecodeMultipleTransfer()
{
    var instruction = ArmDecoder.Decode(0xE88D1004u, 0x1000); // STMIA sp,{r2,r12}
    Equal(ArmOp.StoreMultiple, instruction.Op);
    Equal(13, instruction.Rn);
    Equal((ushort)0x1004, instruction.RegisterList);
    True(instruction.Increment && instruction.PostIndex && !instruction.WriteBack);
}

static void DecodeLogicalImmediate()
{
    Equal(ArmOp.AndImmediate, ArmDecoder.Decode(0xE20000FFu, 0x1000).Op); // AND r0,r0,#255
    Equal(ArmOp.OrImmediate, ArmDecoder.Decode(0xE3811C01u, 0x1004).Op); // ORR r1,r1,#256
    Equal(ArmOp.BitClearImmediate, ArmDecoder.Decode(0xE3C22007u, 0x1008).Op); // BIC r2,r2,#7
    Equal(ArmOp.Nop, ArmDecoder.Decode(0xE320F000u, 0x100C).Op);
    Equal(ArmOp.MoveNotImmediate, ArmDecoder.Decode(0xE3E01001u, 0x100C).Op);
    Equal(ArmOp.TestImmediate, ArmDecoder.Decode(0xE3100003u, 0x100C).Op);
    var shifted = ArmDecoder.Decode(0xE1B01F80u, 0x1010); // MOVS r1,r0,LSL #31
    Equal(ArmOp.MoveRegister, shifted.Op);
    Equal(ArmShiftType.Lsl, shifted.ShiftType);
    Equal((byte)31, shifted.ShiftAmount);
    True(shifted.SetFlags);
}

static void DecodeBlxRegister()
{
    var instruction = ArmDecoder.Decode(0xE12FFF30u, 0x1000); // BLX r0
    Equal(ArmOp.BranchLinkExchange, instruction.Op);
    Equal(0, instruction.Rm);
    True(instruction.TerminatesBlock);
}

static void DecodeFlagSettingArithmetic()
{
    var instruction = ArmDecoder.Decode(0xE2900001u, 0x1000); // ADDS r0,r0,#1
    Equal(ArmOp.AddImmediate, instruction.Op);
    True(instruction.SetFlags);
}

static void DecodeByteMemory()
{
    var load = ArmDecoder.Decode(0xE5D10003u, 0x1000); // LDRB r0,[r1,#3]
    Equal(ArmOp.LoadByteImmediate, load.Op);
    Equal(0, load.Rd);
    Equal(1, load.Rn);
    Equal(3u, load.Immediate);
    Equal(ArmOp.StoreByteImmediate, ArmDecoder.Decode(0xE5C10003u, 0x1004).Op);
}

static void DecodeSignedMemoryAndTls()
{
    Equal(ArmOp.LoadSignedByteImmediate, ArmDecoder.Decode(0xE1D000D0u, 0x1000).Op);
    Equal(ArmOp.LoadSignedHalfwordImmediate, ArmDecoder.Decode(0xE1D000F2u, 0x1004).Op);
    var tls = ArmDecoder.Decode(0xEE1D4F70u, 0x1008);
    Equal(ArmOp.ReadThreadPointer, tls.Op);
    Equal(4, tls.Rd);
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

static void ExecuteMemoryWriteback()
{
    // STR r0,[r1,#4]!; LDR r2,[r1],#-4; BX lr
    var block = ArmToIrTranslator.TranslateBlock(Words(0xE5A10004u, 0xE4112004u, 0xE12FFF1Eu), 0x100000);
    var cpu = new ReferenceCpu();
    var memory = new GuestMemory(0x200000, 64);
    cpu.Registers[0] = 0xAABBCCDDu;
    cpu.Registers[1] = 0x200000u;
    IrInterpreter.Execute(block, cpu, memory);
    Equal(0xAABBCCDDu, memory.Load32(0x200004));
    Equal(0xAABBCCDDu, cpu.Registers[2]);
    Equal(0x200000u, cpu.Registers[1]);
}

static void ExecuteByteAndLogical()
{
    // STRB r0,[r1,#3]; LDRB r2,[r1,#3]; BIC r3,r2,#7; ORR r3,r3,#1; BX lr
    var block = ArmToIrTranslator.TranslateBlock(
        Words(0xE5C10003u, 0xE5D12003u, 0xE3C23007u, 0xE3833001u, 0xE12FFF1Eu), 0x100000);
    var cpu = new ReferenceCpu();
    var memory = new GuestMemory(0x200000, 64);
    cpu.Registers[0] = 0x123456ABu;
    cpu.Registers[1] = 0x200000u;
    IrInterpreter.Execute(block, cpu, memory);
    Equal((byte)0xAB, memory.Load8(0x200003));
    Equal(0xABu, cpu.Registers[2]);
    Equal(0xA9u, cpu.Registers[3]);
}

static void ExecuteSignedMemoryAndTls()
{
    // LDRSB r0,[r1]; LDRSH r2,[r1,#2]; MRC p15,0,r4,c13,c0,3; BX lr
    var block = ArmToIrTranslator.TranslateBlock(
        Words(0xE1D100D0u, 0xE1D120F2u, 0xEE1D4F70u, 0xE12FFF1Eu), 0x100000);
    var cpu = new ReferenceCpu { ThreadPointer = 0xDEAD0000u };
    var memory = new GuestMemory(0x200000, 64);
    cpu.Registers[1] = 0x200000;
    memory.Store8(0x200000, 0x80);
    memory.Store16(0x200002, 0x8001);
    IrInterpreter.Execute(block, cpu, memory);
    Equal(0xFFFFFF80u, cpu.Registers[0]);
    Equal(0xFFFF8001u, cpu.Registers[2]);
    Equal(0xDEAD0000u, cpu.Registers[4]);
}

static void ExecuteShiftedLogicalFlags()
{
    // MOV r0,#1; MOVS r1,r0,LSL #31; ANDS r2,r1,#0x80000000; BX lr
    var block = ArmToIrTranslator.TranslateBlock(
        Words(0xE3A00001u, 0xE1B01F80u, 0xE2112102u, 0xE12FFF1Eu), 0x100000);
    var cpu = new ReferenceCpu();
    IrInterpreter.Execute(block, cpu, new GuestMemory(0x200000, 64));
    Equal(0x80000000u, cpu.Registers[1]);
    Equal(0x80000000u, cpu.Registers[2]);
    True(cpu.Negative && !cpu.Zero && cpu.Carry && !cpu.Overflow);
}

static void ExecuteArithmeticFlags()
{
    // MVN r0,#0; ADDS r1,r0,#1; TST r0,#1; SUBS r2,r1,#1; BX lr
    var block = ArmToIrTranslator.TranslateBlock(
        Words(0xE3E00000u, 0xE2901001u, 0xE3100001u, 0xE2512001u, 0xE12FFF1Eu), 0x100000);
    var cpu = new ReferenceCpu();
    IrInterpreter.Execute(block, cpu, new GuestMemory(0x200000, 64));
    Equal(0u, cpu.Registers[1]);
    Equal(uint.MaxValue, cpu.Registers[2]);
    True(cpu.Negative && !cpu.Zero && !cpu.Carry && !cpu.Overflow);
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

static void ExecutePushPop()
{
    // PUSH {r4,lr}; MOV r4,#42; POP {r4,pc}
    var block = ArmToIrTranslator.TranslateBlock(Words(0xE92D4010u, 0xE3A0402Au, 0xE8BD8010u), 0x1000);
    var cpu = new ReferenceCpu();
    var memory = new GuestMemory(0x200000, 0x100);
    cpu.Registers[4] = 7;
    cpu.Registers[13] = 0x200100;
    cpu.Registers[14] = 0x12345678;
    var result = IrInterpreter.Execute(block, cpu, memory);
    Equal(7u, cpu.Registers[4]);
    Equal(0x200100u, cpu.Registers[13]);
    Equal(HaltReason.JumpRegister, result.Reason);
    EqualNullableUInt(0x12345678u, result.Target);
}

static void ExecuteMultipleTransfer()
{
    // STMIA r1!,{r0,r2}; SUB r1,r1,#8; LDMIA r1!,{r3,r4}; BX lr
    var block = ArmToIrTranslator.TranslateBlock(
        Words(0xE8A10005u, 0xE2411008u, 0xE8B10018u, 0xE12FFF1Eu), 0x1000);
    var cpu = new ReferenceCpu();
    var memory = new GuestMemory(0x200000, 0x100);
    cpu.Registers[0] = 11;
    cpu.Registers[1] = 0x200020;
    cpu.Registers[2] = 22;
    IrInterpreter.Execute(block, cpu, memory);
    Equal(11u, cpu.Registers[3]);
    Equal(22u, cpu.Registers[4]);
    Equal(0x200028u, cpu.Registers[1]);
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

static void DecompressBackwardLz()
{
    // Three trailing literals seed a 15-byte match. Distinct seed bytes catch copy-index errors.
    byte[] compressed = [0, 0, 0, 0x00, 0xC0, (byte)'A', (byte)'B', (byte)'C', 0x10,
        0x11, 0x00, 0x00, 0x08, 0x01, 0x00, 0x00, 0x00];
    var output = BackwardLz.Decompress(compressed);
    Equal(18, output.Length);
    Equal("ABCABCABCABCABCABC", System.Text.Encoding.ASCII.GetString(output));
}

static void LoadCartridgeExecutable()
{
    var root = Path.Combine(Path.GetTempPath(), "PocketRecompTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var path = Path.Combine(root, "fixture.3ds");
        CreateSyntheticCartridge(path);
        var executable = CartridgeReader.LoadExecutableAsync(path).GetAwaiter().GetResult();
        Equal("CTR-P-TEST", executable.Partition.Header.ProductCode);
        Equal("TestApp", executable.CodeSet.Name);
        Equal(0x00100000u, executable.CodeSet.TextAddress);
        Equal(0x1000, executable.Code.Length);
        Equal(0xE12FFF1Eu, BinaryPrimitives.ReadUInt32LittleEndian(executable.Code));
        Equal(64, executable.MappedCodeSha256.Length);
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static void PrepareNativeBuild()
{
    var root = Path.Combine(Path.GetTempPath(), "PocketRecompTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var cartridgePath = Path.Combine(root, "fixture.3ds");
        var outputPath = Path.Combine(root, "output");
        CreateSyntheticCartridge(cartridgePath);
        var result = NativeBuildPipeline.PrepareAsync(cartridgePath, outputPath, []).GetAwaiter().GetResult();
        Equal(NativeBuildStatus.NativeLinkPending, result.Report.Status);
        Equal(1, result.Report.ReachableBlocks);
        Equal(1, result.Report.ReachableInstructions);
        Equal(0, result.Report.UnsupportedEncodings.Count);
        True(!result.Report.NativeExecutableProduced);
        True(File.Exists(result.ReportPath));
        True(File.ReadAllText(result.ReportPath).Contains("native-link-pending", StringComparison.Ordinal));
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static void CreateSyntheticCartridge(string path)
{
    var image = new byte[0x2000];
    "NCSD"u8.CopyTo(image.AsSpan(0x100));
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x104), 0x10);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(0x108), 0x000400000FF40A00);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x120), 1);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x124), 0x0F);

    const int ncch = 0x200;
    "NCCH"u8.CopyTo(image.AsSpan(ncch + 0x100));
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(ncch + 0x104), 0x0F);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(ncch + 0x108), 0x000400000FF40A00);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(ncch + 0x118), 0x000400000FF40A00);
    "CTR-P-TEST"u8.CopyTo(image.AsSpan(ncch + 0x150));
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(ncch + 0x180), 0x400);
    image[ncch + 0x18F] = 0x04;
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(ncch + 0x1A0), 3);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(ncch + 0x1A4), 9);

    const int exheader = ncch + 0x200;
    "TestApp"u8.CopyTo(image.AsSpan(exheader));
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(exheader + 0x10), 0x00100000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(exheader + 0x14), 1);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(exheader + 0x18), 4);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(exheader + 0x1C), 0x4000);

    const int exefs = ncch + 0x600;
    ".code"u8.CopyTo(image.AsSpan(exefs));
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(exefs + 12), 0x1000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(exefs + 0x200), 0xE12FFF1E);
    File.WriteAllBytes(path, image);
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
