using PocketRecomp.Core.Arm;

namespace PocketRecomp.Core.Ir;

public enum IrOp
{
    GuardCondition,
    MoveConstant,
    MoveRegister,
    MoveNotConstant,
    MoveNotRegister,
    AndConstant,
    AndRegister,
    OrConstant,
    OrRegister,
    BitClearConstant,
    BitClearRegister,
    AddConstant,
    AddRegister,
    SubtractConstant,
    SubtractRegister,
    CompareConstant,
    CompareRegister,
    TestConstant,
    TestRegister,
    Load32,
    Store32,
    Load32PreWriteback,
    Store32PreWriteback,
    Load32PostWriteback,
    Store32PostWriteback,
    Load8,
    Store8,
    Load16,
    Store16,
    LoadSigned8,
    LoadSigned16,
    Load8PreWriteback,
    Store8PreWriteback,
    Load8PostWriteback,
    Store8PostWriteback,
    LoadMultiple,
    StoreMultiple,
    Push,
    Pop,
    Jump,
    Call,
    CallRegister,
    JumpRegister,
    SupervisorCall,
    ReadThreadPointer,
    Unsupported,
    Nop
}

public readonly record struct IrInstruction(
    IrOp Op,
    int Destination = -1,
    int Source = -1,
    ulong Value = 0,
    uint GuestAddress = 0,
    uint RawInstruction = 0,
    ArmShiftType ShiftType = ArmShiftType.Lsl,
    byte ShiftAmount = 0,
    bool SetFlags = false,
    bool UpdateCarry = false,
    bool CarryValue = false);

public sealed record IrBlock(uint Address, IReadOnlyList<IrInstruction> Instructions);
