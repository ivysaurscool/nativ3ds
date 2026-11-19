using PocketRecomp.Core.Arm;

namespace PocketRecomp.Core.Ir;

public static class ArmToIrTranslator
{
    public static IrBlock TranslateBlock(ReadOnlySpan<byte> code, uint baseAddress, int offset = 0, int maxInstructions = 256)
    {
        if ((offset & 3) != 0) throw new ArgumentException("ARM code offset must be 4-byte aligned.", nameof(offset));
        if (offset < 0 || offset > code.Length) throw new ArgumentOutOfRangeException(nameof(offset));

        var result = new List<IrInstruction>();
        var startAddress = baseAddress + (uint)offset;
        for (var cursor = offset; cursor + 4 <= code.Length && result.Count < maxInstructions; cursor += 4)
        {
            var word = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(code[cursor..]);
            var address = baseAddress + (uint)cursor;
            var instruction = ArmDecoder.Decode(word, address);
            if (instruction.Condition != ArmCondition.Al)
                result.Add(new(IrOp.GuardCondition, Value: (uint)instruction.Condition, GuestAddress: address));

            result.Add(Lower(instruction));
            if (instruction.TerminatesBlock) break;
        }
        return new(startAddress, result);
    }

    private static IrInstruction Lower(ArmInstruction i) => i.Op switch
    {
        ArmOp.MoveImmediate => New(IrOp.MoveConstant, i.Rd, value: i.Immediate),
        ArmOp.AddImmediate => New(IrOp.AddConstant, i.Rd, i.Rn, i.Immediate),
        ArmOp.SubtractImmediate => New(IrOp.SubtractConstant, i.Rd, i.Rn, i.Immediate),
        ArmOp.CompareImmediate => New(IrOp.CompareConstant, source: i.Rn, value: i.Immediate),
        ArmOp.LoadImmediate => New(IrOp.Load32, i.Rd, i.Rn, i.Immediate),
        ArmOp.StoreImmediate => New(IrOp.Store32, source: i.Rd, value: ((ulong)(uint)i.Rn << 32) | i.Immediate),
        ArmOp.Branch => New(IrOp.Jump, value: i.Target),
        ArmOp.BranchLink => New(IrOp.Call, value: i.Target),
        ArmOp.BranchExchange => New(IrOp.JumpRegister, source: i.Rm),
        ArmOp.SoftwareInterrupt => New(IrOp.SupervisorCall, value: i.Immediate),
        _ => New(IrOp.Unsupported, value: i.Word)
    };

    private static IrInstruction New(IrOp op, int destination = -1, int source = -1, ulong value = 0) =>
        new(op, destination, source, value);
}
