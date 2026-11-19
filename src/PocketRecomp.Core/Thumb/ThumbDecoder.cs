using System.Buffers.Binary;
using PocketRecomp.Core.Arm;

namespace PocketRecomp.Core.Thumb;

/// <summary>Strict decoder for the first Thumb-1 subset used by ARM11 homebrew.</summary>
public static class ThumbDecoder
{
    public static ThumbInstruction Decode(ReadOnlySpan<byte> code, uint address)
    {
        if (code.Length < 2) throw new ArgumentException("Thumb decoding requires at least two bytes.", nameof(code));
        if ((address & 1) != 0) throw new ArgumentException("Thumb instruction address must be halfword aligned.", nameof(address));
        var first = BinaryPrimitives.ReadUInt16LittleEndian(code);

        // Thumb-1 BL uses two halfwords: 11110 high-offset, 11111 low-offset.
        if ((first & 0xF800) == 0xF000 && code.Length >= 4)
        {
            var second = BinaryPrimitives.ReadUInt16LittleEndian(code[2..]);
            if ((second & 0xF800) == 0xF800)
            {
                var high = SignExtend(first & 0x07FF, 11) << 12;
                var low = (second & 0x07FF) << 1;
                var target = unchecked((uint)((int)address + 4 + high + low));
                return new(address, (uint)(first | second << 16), 4, ThumbOp.BranchLink, Target: target);
            }
        }

        return Decode16(first, address);
    }

    public static ThumbInstruction Decode16(ushort value, uint address)
    {
        // Shift by immediate: 000 op imm5 Rs Rd. op=3 belongs to add/subtract format.
        if ((value & 0xE000) == 0 && ((value >> 11) & 3) < 3)
        {
            var op = ((value >> 11) & 3) switch
            {
                0 => ThumbOp.LogicalShiftLeftImmediate,
                1 => ThumbOp.LogicalShiftRightImmediate,
                _ => ThumbOp.ArithmeticShiftRightImmediate
            };
            var amount = (uint)((value >> 6) & 0x1F);
            if (op is ThumbOp.LogicalShiftRightImmediate or ThumbOp.ArithmeticShiftRightImmediate && amount == 0) amount = 32;
            return new(address, value, 2, op, Rd: value & 7, Rm: (value >> 3) & 7, Immediate: amount);
        }

        // Add/subtract register or 3-bit immediate: 00011 I Op Rn/imm3 Rs Rd.
        if ((value & 0xF800) == 0x1800)
        {
            var immediate = (value & 0x0400) != 0;
            var subtract = (value & 0x0200) != 0;
            var operand = (value >> 6) & 7;
            var op = (immediate, subtract) switch
            {
                (false, false) => ThumbOp.AddRegister,
                (false, true) => ThumbOp.SubtractRegister,
                (true, false) => ThumbOp.AddImmediate3,
                _ => ThumbOp.SubtractImmediate3
            };
            return new(address, value, 2, op, Rd: value & 7, Rn: (value >> 3) & 7,
                Rm: immediate ? -1 : operand, Immediate: immediate ? (uint)operand : 0);
        }

        // MOV/CMP/ADD/SUB immediate: 001 Op Rd imm8.
        if ((value & 0xE000) == 0x2000)
        {
            var op = ((value >> 11) & 3) switch
            {
                0 => ThumbOp.MoveImmediate,
                1 => ThumbOp.CompareImmediate,
                2 => ThumbOp.AddImmediate8,
                _ => ThumbOp.SubtractImmediate8
            };
            return new(address, value, 2, op, Rd: (value >> 8) & 7, Immediate: (uint)(value & 0xFF));
        }

        // LDR literal: 01001 Rd word8.
        if ((value & 0xF800) == 0x4800)
        {
            var literalAddress = ((address + 4) & ~3u) + (uint)((value & 0xFF) << 2);
            return new(address, value, 2, ThumbOp.LoadLiteral, Rd: (value >> 8) & 7, Target: literalAddress);
        }

        // Word STR/LDR immediate: 0110 L imm5 Rn Rd.
        if ((value & 0xF000) == 0x6000)
        {
            var load = (value & 0x0800) != 0;
            return new(address, value, 2, load ? ThumbOp.LoadWordImmediate : ThumbOp.StoreWordImmediate,
                Rd: value & 7, Rn: (value >> 3) & 7, Immediate: (uint)(((value >> 6) & 0x1F) << 2));
        }

        // BX/BLX register. BLX is rejected until exchange-and-link semantics are represented.
        if ((value & 0xFF00) == 0x4700)
        {
            var link = (value & 0x0080) != 0;
            var rm = ((value >> 3) & 0xF);
            return new(address, value, 2, link ? ThumbOp.Unknown : ThumbOp.BranchExchange, Rm: rm);
        }

        if ((value & 0xFE00) == 0xB400)
        {
            var registers = (ushort)(value & 0xFF);
            if ((value & 0x0100) != 0) registers |= 1 << 14;
            return new(address, value, 2, ThumbOp.Push, RegisterList: registers);
        }
        if ((value & 0xFE00) == 0xBC00)
        {
            var registers = (ushort)(value & 0xFF);
            if ((value & 0x0100) != 0) registers |= 1 << 15;
            return new(address, value, 2, ThumbOp.Pop, RegisterList: registers);
        }

        // Conditional branch/SVC: 1101 cond imm8.
        if ((value & 0xF000) == 0xD000)
        {
            var condition = (value >> 8) & 0xF;
            if (condition == 0xF)
                return new(address, value, 2, ThumbOp.SupervisorCall, Immediate: (uint)(value & 0xFF));
            if (condition == 0xE) return new(address, value, 2, ThumbOp.Unknown);
            var displacement = SignExtend(value & 0xFF, 8) << 1;
            var target = unchecked((uint)((int)address + 4 + displacement));
            return new(address, value, 2, ThumbOp.BranchConditional, Target: target, Condition: (ArmCondition)condition);
        }

        // Unconditional branch: 11100 imm11.
        if ((value & 0xF800) == 0xE000)
        {
            var displacement = SignExtend(value & 0x7FF, 11) << 1;
            var target = unchecked((uint)((int)address + 4 + displacement));
            return new(address, value, 2, ThumbOp.Branch, Target: target);
        }

        return new(address, value, 2, ThumbOp.Unknown);
    }

    private static int SignExtend(int value, int bits)
    {
        var shift = 32 - bits;
        return value << shift >> shift;
    }
}
