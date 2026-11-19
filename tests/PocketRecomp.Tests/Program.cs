using System.Buffers.Binary;
using PocketRecomp.Core.Arm;
using PocketRecomp.Core.Analysis;
using PocketRecomp.Core.Codegen;
using PocketRecomp.Core.Display;
using PocketRecomp.Core.Ir;
using PocketRecomp.Core.Runtime;
using PocketRecomp.Core.Projects;

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

static void True(bool condition)
{
    if (!condition) throw new Exception("condition was false");
}
