namespace PocketRecomp.Core.Arm;

public enum ArmCondition : byte
{
    Eq = 0, Ne, Cs, Cc, Mi, Pl, Vs, Vc, Hi, Ls, Ge, Lt, Gt, Le, Al
}

public enum ArmShiftType : byte { Lsl, Lsr, Asr, Ror }

public enum ArmOp
{
    Unknown,
    Branch,
    BranchLink,
    BranchExchange,
    BranchLinkExchange,
    Nop,
    MoveImmediate,
    MoveRegister,
    MoveNotImmediate,
    MoveNotRegister,
    AndImmediate,
    AndRegister,
    OrImmediate,
    OrRegister,
    BitClearImmediate,
    BitClearRegister,
    AddImmediate,
    AddRegister,
    SubtractImmediate,
    SubtractRegister,
    CompareImmediate,
    CompareRegister,
    TestImmediate,
    TestRegister,
    LoadImmediate,
    StoreImmediate,
    LoadByteImmediate,
    StoreByteImmediate,
    LoadHalfwordImmediate,
    StoreHalfwordImmediate,
    LoadSignedByteImmediate,
    LoadSignedHalfwordImmediate,
    LoadMultiple,
    StoreMultiple,
    Push,
    Pop,
    SoftwareInterrupt,
    ReadThreadPointer
}

public readonly record struct ArmInstruction(
    uint Address,
    uint Word,
    ArmCondition Condition,
    ArmOp Op,
    int Rd = -1,
    int Rn = -1,
    int Rm = -1,
    uint Immediate = 0,
    uint Target = 0,
    bool SetFlags = false,
    ushort RegisterList = 0,
    bool WriteBack = false,
    bool PostIndex = false,
    ArmShiftType ShiftType = ArmShiftType.Lsl,
    byte ShiftAmount = 0,
    bool UpdateCarry = false,
    bool CarryValue = false,
    bool Increment = true)
{
    public bool TerminatesBlock => Op is ArmOp.Branch or ArmOp.BranchLink or ArmOp.BranchExchange or ArmOp.BranchLinkExchange or ArmOp.SoftwareInterrupt ||
        ((Op is ArmOp.Pop or ArmOp.LoadMultiple) && (RegisterList & 0x8000) != 0);
}
