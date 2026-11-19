namespace PocketRecomp.Core.Arm;

public enum ArmCondition : byte
{
    Eq = 0, Ne, Cs, Cc, Mi, Pl, Vs, Vc, Hi, Ls, Ge, Lt, Gt, Le, Al
}

public enum ArmOp
{
    Unknown,
    Branch,
    BranchLink,
    BranchExchange,
    MoveImmediate,
    AddImmediate,
    SubtractImmediate,
    CompareImmediate,
    LoadImmediate,
    StoreImmediate,
    SoftwareInterrupt
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
    bool SetFlags = false)
{
    public bool TerminatesBlock => Op is ArmOp.Branch or ArmOp.BranchLink or ArmOp.BranchExchange or ArmOp.SoftwareInterrupt;
}
