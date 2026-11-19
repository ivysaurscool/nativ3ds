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
        ArmOp.MoveRegister => New(IrOp.MoveRegister, i, i.Rd, value: (uint)i.Rm),
        ArmOp.MoveNotImmediate => New(IrOp.MoveNotConstant, i, i.Rd, value: i.Immediate),
        ArmOp.MoveNotRegister => New(IrOp.MoveNotRegister, i, i.Rd, value: (uint)i.Rm),
        ArmOp.AndImmediate => New(IrOp.AndConstant, i, i.Rd, i.Rn, i.Immediate),
        ArmOp.AndRegister => New(IrOp.AndRegister, i, i.Rd, i.Rn, (uint)i.Rm),
        ArmOp.OrImmediate => New(IrOp.OrConstant, i, i.Rd, i.Rn, i.Immediate),
        ArmOp.OrRegister => New(IrOp.OrRegister, i, i.Rd, i.Rn, (uint)i.Rm),
        ArmOp.BitClearImmediate => New(IrOp.BitClearConstant, i, i.Rd, i.Rn, i.Immediate),
        ArmOp.BitClearRegister => New(IrOp.BitClearRegister, i, i.Rd, i.Rn, (uint)i.Rm),
        ArmOp.AddImmediate => New(IrOp.AddConstant, i, i.Rd, i.Rn, i.Immediate),
        ArmOp.AddRegister => New(IrOp.AddRegister, i, i.Rd, i.Rn, (uint)i.Rm),
        ArmOp.SubtractImmediate => New(IrOp.SubtractConstant, i, i.Rd, i.Rn, i.Immediate),
        ArmOp.SubtractRegister => New(IrOp.SubtractRegister, i, i.Rd, i.Rn, (uint)i.Rm),
        ArmOp.CompareImmediate => New(IrOp.CompareConstant, i, source: i.Rn, value: i.Immediate),
        ArmOp.CompareRegister => New(IrOp.CompareRegister, i, source: i.Rn, value: (uint)i.Rm),
        ArmOp.TestImmediate => New(IrOp.TestConstant, i, source: i.Rn, value: i.Immediate),
        ArmOp.TestRegister => New(IrOp.TestRegister, i, source: i.Rn, value: (uint)i.Rm),
        ArmOp.LoadImmediate => LowerLoad(i, byteTransfer: false),
        ArmOp.StoreImmediate => LowerStore(i, byteTransfer: false),
        ArmOp.LoadByteImmediate => LowerLoad(i, byteTransfer: true),
        ArmOp.StoreByteImmediate => LowerStore(i, byteTransfer: true),
        ArmOp.LoadHalfwordImmediate => New(IrOp.Load16, i, i.Rd, i.Rn, i.Immediate),
        ArmOp.StoreHalfwordImmediate => New(IrOp.Store16, i, source: i.Rd, value: ((ulong)(uint)i.Rn << 32) | i.Immediate),
        ArmOp.LoadSignedByteImmediate => New(IrOp.LoadSigned8, i, i.Rd, i.Rn, i.Immediate),
        ArmOp.LoadSignedHalfwordImmediate => New(IrOp.LoadSigned16, i, i.Rd, i.Rn, i.Immediate),
        ArmOp.LoadMultiple => New(IrOp.LoadMultiple, i, value: PackMultiple(i)),
        ArmOp.StoreMultiple => New(IrOp.StoreMultiple, i, value: PackMultiple(i)),
        ArmOp.Push => New(IrOp.Push, i, value: i.RegisterList),
        ArmOp.Pop => New(IrOp.Pop, i, value: i.RegisterList),
        ArmOp.Branch => New(IrOp.Jump, i, value: i.Target),
        ArmOp.BranchLink => New(IrOp.Call, i, value: i.Target),
        ArmOp.BranchLinkExchange => New(IrOp.CallRegister, i, source: i.Rm),
        ArmOp.BranchExchange => New(IrOp.JumpRegister, i, source: i.Rm),
        ArmOp.Nop => New(IrOp.Nop, i),
        ArmOp.SoftwareInterrupt => New(IrOp.SupervisorCall, i, value: i.Immediate),
        ArmOp.ReadThreadPointer => New(IrOp.ReadThreadPointer, i, i.Rd),
        _ => New(IrOp.Unsupported, i, value: i.Word)
    };

    private static IrInstruction LowerLoad(ArmInstruction i, bool byteTransfer)
    {
        var op = (byteTransfer, i.PostIndex, i.WriteBack) switch
        {
            (false, true, _) => IrOp.Load32PostWriteback,
            (false, false, true) => IrOp.Load32PreWriteback,
            (false, false, false) => IrOp.Load32,
            (true, true, _) => IrOp.Load8PostWriteback,
            (true, false, true) => IrOp.Load8PreWriteback,
            _ => IrOp.Load8
        };
        return New(op, i, i.Rd, i.Rn, i.Immediate);
    }

    private static IrInstruction LowerStore(ArmInstruction i, bool byteTransfer)
    {
        var op = (byteTransfer, i.PostIndex, i.WriteBack) switch
        {
            (false, true, _) => IrOp.Store32PostWriteback,
            (false, false, true) => IrOp.Store32PreWriteback,
            (false, false, false) => IrOp.Store32,
            (true, true, _) => IrOp.Store8PostWriteback,
            (true, false, true) => IrOp.Store8PreWriteback,
            _ => IrOp.Store8
        };
        return New(op, i, source: i.Rd, value: ((ulong)(uint)i.Rn << 32) | i.Immediate);
    }

    private static ulong PackMultiple(ArmInstruction i) => i.RegisterList |
        ((ulong)(uint)i.Rn << 16) |
        (i.PostIndex ? 0UL : 1UL << 20) |
        (i.Increment ? 1UL << 21 : 0) |
        (i.WriteBack ? 1UL << 22 : 0);

    private static IrInstruction New(IrOp op, ArmInstruction sourceInstruction,
        int destination = -1, int source = -1, ulong value = 0) =>
        new(op, destination, source, value, sourceInstruction.Address, sourceInstruction.Word,
            sourceInstruction.ShiftType, sourceInstruction.ShiftAmount, sourceInstruction.SetFlags,
            sourceInstruction.UpdateCarry, sourceInstruction.CarryValue);
}
