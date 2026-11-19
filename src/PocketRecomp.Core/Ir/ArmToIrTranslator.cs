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
        ArmOp.MoveImmediate => New(IrOp.MoveConstant, i, i.Rd, value: i.Immediate),
        ArmOp.AddImmediate => New(IrOp.AddConstant, i, i.Rd, i.Rn, i.Immediate),
        ArmOp.SubtractImmediate => New(IrOp.SubtractConstant, i, i.Rd, i.Rn, i.Immediate),
        ArmOp.CompareImmediate => New(IrOp.CompareConstant, i, source: i.Rn, value: i.Immediate),
        ArmOp.LoadImmediate => New(IrOp.Load32, i, i.Rd, i.Rn, i.Immediate),
        ArmOp.StoreImmediate => New(IrOp.Store32, i, source: i.Rd, value: ((ulong)(uint)i.Rn << 32) | i.Immediate),
        ArmOp.Branch => New(IrOp.Jump, i, value: i.Target),
        ArmOp.BranchLink => New(IrOp.Call, i, value: i.Target),
        ArmOp.BranchExchange => New(IrOp.JumpRegister, i, source: i.Rm),
        ArmOp.SoftwareInterrupt => New(IrOp.SupervisorCall, i, value: i.Immediate),
        _ => New(IrOp.Unsupported, i, value: i.Word)
    };

    private static IrInstruction New(IrOp op, ArmInstruction sourceInstruction,
        int destination = -1, int source = -1, ulong value = 0) =>
        new(op, destination, source, value, sourceInstruction.Address, sourceInstruction.Word);
}
