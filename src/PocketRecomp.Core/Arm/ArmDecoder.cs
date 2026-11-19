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

        // BLX Rm.
        if ((word & 0x0FFF_FFF0u) == 0x012F_FF30u)
            return new(address, word, condition, ArmOp.BranchLinkExchange, Rm: (int)(word & 0xF));

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

        // MRC p15,0,Rd,c13,c0,3: ARMv6 thread ID / 3DS TLS base.
        if ((word & 0x0FFF_0FFFu) == 0x0E1D_0F70u)
            return new(address, word, condition, ArmOp.ReadThreadPointer, Rd: (int)((word >> 12) & 0xF));

        if ((word & 0x0FFF_FFFFu) == 0x0320_F000u)
            return new(address, word, condition, ArmOp.Nop);

        // Common stack aliases: STMDB sp!,reglist (PUSH) and LDMIA sp!,reglist (POP).
        if ((word & 0x0FFF_0000u) == 0x092D_0000u)
            return new(address, word, condition, ArmOp.Push, RegisterList: (ushort)word);
        if ((word & 0x0FFF_0000u) == 0x08BD_0000u)
            return new(address, word, condition, ArmOp.Pop, RegisterList: (ushort)word);

        // LDM/STM without the privileged S-bit behavior. Registers are transferred in ascending order.
        if ((word & 0x0E40_0000u) == 0x0800_0000u)
        {
            var load = (word & 0x0010_0000u) != 0;
            var preIndex = (word & 0x0100_0000u) != 0;
            var increment = (word & 0x0080_0000u) != 0;
            var writeBack = (word & 0x0020_0000u) != 0;
            return new(address, word, condition, load ? ArmOp.LoadMultiple : ArmOp.StoreMultiple,
                Rn: (int)((word >> 16) & 0xF), RegisterList: (ushort)word,
                WriteBack: writeBack, PostIndex: !preIndex, Increment: increment);
        }

        // Single data transfer with an immediate offset. Post-indexed transfers always write back.
        if ((word & 0x0E00_0000u) == 0x0400_0000u)
        {
            var load = (word & 0x0010_0000u) != 0;
            var byteTransfer = (word & 0x0040_0000u) != 0;
            var up = (word & 0x0080_0000u) != 0;
            var postIndex = (word & 0x0100_0000u) == 0;
            var writeBack = postIndex || (word & 0x0020_0000u) != 0;
            var offset = word & 0xFFFu;
            if (!up) offset = unchecked(0u - offset);
            var op = byteTransfer
                ? load ? ArmOp.LoadByteImmediate : ArmOp.StoreByteImmediate
                : load ? ArmOp.LoadImmediate : ArmOp.StoreImmediate;
            return new(address, word, condition, op,
                Rd: (int)((word >> 12) & 0xF), Rn: (int)((word >> 16) & 0xF), Immediate: offset,
                WriteBack: writeBack, PostIndex: postIndex);
        }

        // Extra load/store immediate forms. Writeback/register-offset and doubleword forms land later.
        if ((word & 0x0E40_0090u) == 0x0040_0090u)
        {
            var preIndex = (word & 0x0100_0000u) != 0;
            var writeBack = (word & 0x0020_0000u) != 0;
            var load = (word & 0x0010_0000u) != 0;
            var kind = (word >> 5) & 0x3;
            if (!preIndex || writeBack || kind == 0) return new(address, word, condition, ArmOp.Unknown);
            var up = (word & 0x0080_0000u) != 0;
            var offset = ((word >> 4) & 0xF0u) | (word & 0xFu);
            if (!up) offset = unchecked(0u - offset);
            var op = (load, kind) switch
            {
                (true, 1) => ArmOp.LoadHalfwordImmediate,
                (false, 1) => ArmOp.StoreHalfwordImmediate,
                (true, 2) => ArmOp.LoadSignedByteImmediate,
                (true, 3) => ArmOp.LoadSignedHalfwordImmediate,
                _ => ArmOp.Unknown
            };
            return new(address, word, condition, op, Rd: (int)((word >> 12) & 0xF),
                Rn: (int)((word >> 16) & 0xF), Immediate: offset);
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
            // Flag-setting arithmetic needs carry/overflow lowering. Logical operations only write N/Z/C.
            var op = opcode switch
            {
                0xD => ArmOp.MoveImmediate,
                0xF => ArmOp.MoveNotImmediate,
                0x0 => ArmOp.AndImmediate,
                0xC => ArmOp.OrImmediate,
                0xE => ArmOp.BitClearImmediate,
                0x4 => ArmOp.AddImmediate,
                0x2 => ArmOp.SubtractImmediate,
                0x8 => ArmOp.TestImmediate,
                0xA => ArmOp.CompareImmediate,
                _ => ArmOp.Unknown
            };
            return new(address, word, condition, op, rd, rn, Immediate: immediate, SetFlags: setFlags,
                UpdateCarry: rotate != 0, CarryValue: (immediate & 0x8000_0000u) != 0);
        }

        // Data processing with an immediate-shifted register operand (bit 4 clear).
        if ((word & 0x0E00_0010u) == 0)
        {
            var opcode = (word >> 21) & 0xF;
            var rd = (int)((word >> 12) & 0xF);
            var rn = (int)((word >> 16) & 0xF);
            var rm = (int)(word & 0xF);
            var shiftType = (ArmShiftType)((word >> 5) & 0x3);
            var shiftAmount = (byte)((word >> 7) & 0x1F);
            var setFlags = (word & 0x0010_0000u) != 0;
            var op = opcode switch
            {
                0xD => ArmOp.MoveRegister,
                0xF => ArmOp.MoveNotRegister,
                0x0 => ArmOp.AndRegister,
                0xC => ArmOp.OrRegister,
                0xE => ArmOp.BitClearRegister,
                0x4 => ArmOp.AddRegister,
                0x2 => ArmOp.SubtractRegister,
                0x8 => ArmOp.TestRegister,
                0xA => ArmOp.CompareRegister,
                _ => ArmOp.Unknown
            };
            return new(address, word, condition, op, rd, rn, rm, SetFlags: setFlags,
                ShiftType: shiftType, ShiftAmount: shiftAmount);
        }

        return new(address, word, condition, ArmOp.Unknown);
    }
}
