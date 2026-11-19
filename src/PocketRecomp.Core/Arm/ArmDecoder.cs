using System.Numerics;

namespace PocketRecomp.Core.Arm;

/// <summary>Decodes the initial ARM-state subset used by the static recompiler.</summary>
public static class ArmDecoder
{
    public static ArmInstruction Decode(uint word, uint address)
    {
        var condition = (ArmCondition)(word >> 28);

        // BX Rm: cond 0001 0010 1111 1111 1111 0001 Rm
        if ((word & 0x0FFF_FFF0u) == 0x012F_FF10u)
            return new(address, word, condition, ArmOp.BranchExchange, Rm: (int)(word & 0xF));

        // B / BL: cond 101L signed_imm24. PC observes address + 8 in ARM state.
        if ((word & 0x0E00_0000u) == 0x0A00_0000u)
        {
            var link = (word & 0x0100_0000u) != 0;
            var displacement = ((int)(word << 8)) >> 6;
            var target = unchecked((uint)(address + 8 + displacement));
            return new(address, word, condition, link ? ArmOp.BranchLink : ArmOp.Branch, Target: target);
        }

        // SVC/SWI: cond 1111 imm24
        if ((word & 0x0F00_0000u) == 0x0F00_0000u)
            return new(address, word, condition, ArmOp.SoftwareInterrupt, Immediate: word & 0x00FF_FFFFu);

        // Single data transfer, immediate offset (pre/post and writeback are retained for later).
        if ((word & 0x0F60_0000u) == 0x0500_0000u)
        {
            var load = (word & 0x0010_0000u) != 0;
            var up = (word & 0x0080_0000u) != 0;
            var offset = word & 0xFFFu;
            if (!up) offset = unchecked(0u - offset);
            return new(address, word, condition, load ? ArmOp.LoadImmediate : ArmOp.StoreImmediate,
                Rd: (int)((word >> 12) & 0xF), Rn: (int)((word >> 16) & 0xF), Immediate: offset);
        }

        // Data processing with rotated 8-bit immediate.
        if ((word & 0x0E00_0000u) == 0x0200_0000u)
        {
            var opcode = (word >> 21) & 0xF;
            var rotate = (int)(((word >> 8) & 0xF) * 2);
            var immediate = BitOperations.RotateRight(word & 0xFFu, rotate);
            var rd = (int)((word >> 12) & 0xF);
            var rn = (int)((word >> 16) & 0xF);
            var setFlags = (word & 0x0010_0000u) != 0;
            // Flag-setting arithmetic needs carry/overflow lowering, so reject it until that IR lands.
            var op = opcode switch
            {
                0xD when !setFlags => ArmOp.MoveImmediate,
                0x4 when !setFlags => ArmOp.AddImmediate,
                0x2 when !setFlags => ArmOp.SubtractImmediate,
                0xA => ArmOp.CompareImmediate,
                _ => ArmOp.Unknown
            };
            return new(address, word, condition, op, rd, rn, Immediate: immediate, SetFlags: setFlags);
        }

        return new(address, word, condition, ArmOp.Unknown);
    }
}
