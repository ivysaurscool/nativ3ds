namespace PocketRecomp.Core.Ir;

public enum IrOp
{
    GuardCondition,
    MoveConstant,
    AddConstant,
    SubtractConstant,
    CompareConstant,
    Load32,
    Store32,
    Jump,
    Call,
    JumpRegister,
    SupervisorCall,
    Unsupported
}

public readonly record struct IrInstruction(
    IrOp Op,
    int Destination = -1,
    int Source = -1,
    ulong Value = 0,
    uint GuestAddress = 0,
    uint RawInstruction = 0);

public sealed record IrBlock(uint Address, IReadOnlyList<IrInstruction> Instructions);
