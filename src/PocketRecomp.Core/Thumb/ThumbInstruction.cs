using PocketRecomp.Core.Arm;

namespace PocketRecomp.Core.Thumb;

public enum ThumbOp
{
    Unknown,
    LogicalShiftLeftImmediate,
    LogicalShiftRightImmediate,
    ArithmeticShiftRightImmediate,
    AddRegister,
    SubtractRegister,
    AddImmediate3,
    SubtractImmediate3,
    MoveImmediate,
    CompareImmediate,
    AddImmediate8,
    SubtractImmediate8,
    LoadLiteral,
    StoreWordImmediate,
    LoadWordImmediate,
    Push,
    Pop,
    BranchConditional,
    Branch,
    BranchLink,
    BranchExchange,
    SupervisorCall
}

public readonly record struct ThumbInstruction(
    uint Address,
    uint Encoding,
    int Size,
    ThumbOp Op,
    int Rd = -1,
    int Rn = -1,
    int Rm = -1,
    uint Immediate = 0,
    uint Target = 0,
    ArmCondition Condition = ArmCondition.Al,
    ushort RegisterList = 0)
{
    public bool IsSupported => Op != ThumbOp.Unknown;
}
