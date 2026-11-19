using System.Buffers.Binary;
using PocketRecomp.Core.Arm;
using PocketRecomp.Core.Ir;

namespace PocketRecomp.Core.Runtime;

public enum HaltReason { EndOfBlock, Jump, Call, JumpRegister, SupervisorCall }

public sealed record ExecutionResult(HaltReason Reason, uint? Target = null, uint? ServiceId = null);

public sealed class ReferenceCpu
{
    public uint[] Registers { get; } = new uint[16];
    public uint ThreadPointer { get; set; }
    public bool Negative { get; private set; }
    public bool Zero { get; private set; }
    public bool Carry { get; private set; }
    public bool Overflow { get; private set; }

    public void Compare(uint left, uint right)
    {
        var result = left - right;
        Negative = (result & 0x8000_0000u) != 0;
        Zero = result == 0;
        Carry = left >= right;
        Overflow = ((left ^ right) & (left ^ result) & 0x8000_0000u) != 0;
    }

    public uint Add(uint left, uint right, bool setFlags)
    {
        var wide = (ulong)left + right;
        var result = (uint)wide;
        if (setFlags)
        {
            Negative = (result & 0x8000_0000u) != 0;
            Zero = result == 0;
            Carry = wide > uint.MaxValue;
            Overflow = (~(left ^ right) & (left ^ result) & 0x8000_0000u) != 0;
        }
        return result;
    }

    public uint Subtract(uint left, uint right, bool setFlags)
    {
        var result = left - right;
        if (setFlags) Compare(left, right);
        return result;
    }

    public void SetLogicalFlags(uint result, bool updateCarry = false, bool carry = false)
    {
        Negative = (result & 0x8000_0000u) != 0;
        Zero = result == 0;
        if (updateCarry) Carry = carry;
    }

    public bool ConditionPassed(ArmCondition condition) => condition switch
    {
        ArmCondition.Eq => Zero,
        ArmCondition.Ne => !Zero,
        ArmCondition.Cs => Carry,
        ArmCondition.Cc => !Carry,
        ArmCondition.Mi => Negative,
        ArmCondition.Pl => !Negative,
        ArmCondition.Vs => Overflow,
        ArmCondition.Vc => !Overflow,
        ArmCondition.Hi => Carry && !Zero,
        ArmCondition.Ls => !Carry || Zero,
        ArmCondition.Ge => Negative == Overflow,
        ArmCondition.Lt => Negative != Overflow,
        ArmCondition.Gt => !Zero && Negative == Overflow,
        ArmCondition.Le => Zero || Negative != Overflow,
        ArmCondition.Al => true,
        _ => false
    };
}

public sealed class GuestMemory
{
    private readonly byte[] data;
    public uint BaseAddress { get; }
    public int Length => data.Length;

    public GuestMemory(uint baseAddress, int length)
    {
        if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length));
        BaseAddress = baseAddress;
        data = new byte[length];
    }

    public uint Load32(uint address)
    {
        var offset = GetOffset(address, 4);
        return BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
    }

    public byte Load8(uint address) => data[GetOffset(address, 1, alignment: 1)];

    public ushort Load16(uint address)
    {
        var offset = GetOffset(address, 2, alignment: 2);
        return BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
    }

    public uint LoadSigned8(uint address) => unchecked((uint)(int)(sbyte)Load8(address));

    public uint LoadSigned16(uint address) => unchecked((uint)(int)(short)Load16(address));

    public void Store32(uint address, uint value)
    {
        var offset = GetOffset(address, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, 4), value);
    }

    public void Store8(uint address, byte value) => data[GetOffset(address, 1, alignment: 1)] = value;

    public void Store16(uint address, ushort value)
    {
        var offset = GetOffset(address, 2, alignment: 2);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset, 2), value);
    }

    private int GetOffset(uint address, int size, int alignment = 4)
    {
        if (address < BaseAddress) throw new InvalidOperationException($"Guest address 0x{address:X8} is below mapped memory.");
        var offset = (ulong)address - BaseAddress;
        if (offset + (uint)size > (ulong)data.Length)
            throw new InvalidOperationException($"Guest address 0x{address:X8} is outside mapped memory.");
        if ((address & (uint)(alignment - 1)) != 0) throw new InvalidOperationException($"Unaligned guest access at 0x{address:X8}.");
        return checked((int)offset);
    }
}

public static class IrInterpreter
{
    public static ExecutionResult Execute(IrBlock block, ReferenceCpu cpu, GuestMemory memory)
    {
        var executeNext = true;
        foreach (var instruction in block.Instructions)
        {
            if (instruction.Op == IrOp.GuardCondition)
            {
                executeNext = cpu.ConditionPassed((ArmCondition)instruction.Value);
                continue;
            }
            if (!executeNext)
            {
                executeNext = true;
                continue;
            }

            executeNext = true;
            switch (instruction.Op)
            {
                case IrOp.MoveConstant:
                    cpu.Registers[instruction.Destination] = (uint)instruction.Value;
                    ApplyImmediateLogicalFlags(instruction, cpu);
                    break;
                case IrOp.MoveRegister:
                    var moveOperand = ShiftOperand(instruction, cpu, out var moveUpdatesCarry, out var moveCarry);
                    cpu.Registers[instruction.Destination] = moveOperand;
                    if (instruction.SetFlags) cpu.SetLogicalFlags(moveOperand, moveUpdatesCarry, moveCarry);
                    break;
                case IrOp.MoveNotConstant:
                    cpu.Registers[instruction.Destination] = ~(uint)instruction.Value;
                    ApplyImmediateLogicalFlags(instruction, cpu);
                    break;
                case IrOp.MoveNotRegister:
                    var mvnOperand = ShiftOperand(instruction, cpu, out var mvnUpdatesCarry, out var mvnCarry);
                    cpu.Registers[instruction.Destination] = ~mvnOperand;
                    if (instruction.SetFlags)
                        cpu.SetLogicalFlags(cpu.Registers[instruction.Destination], mvnUpdatesCarry, mvnCarry);
                    break;
                case IrOp.AndConstant:
                    cpu.Registers[instruction.Destination] = cpu.Registers[instruction.Source] & (uint)instruction.Value;
                    ApplyImmediateLogicalFlags(instruction, cpu);
                    break;
                case IrOp.AndRegister:
                    var andOperand = ShiftOperand(instruction, cpu, out var andUpdatesCarry, out var andCarry);
                    cpu.Registers[instruction.Destination] = cpu.Registers[instruction.Source] & andOperand;
                    if (instruction.SetFlags)
                        cpu.SetLogicalFlags(cpu.Registers[instruction.Destination], andUpdatesCarry, andCarry);
                    break;
                case IrOp.OrConstant:
                    cpu.Registers[instruction.Destination] = cpu.Registers[instruction.Source] | (uint)instruction.Value;
                    ApplyImmediateLogicalFlags(instruction, cpu);
                    break;
                case IrOp.OrRegister:
                    var orOperand = ShiftOperand(instruction, cpu, out var orUpdatesCarry, out var orCarry);
                    cpu.Registers[instruction.Destination] = cpu.Registers[instruction.Source] | orOperand;
                    if (instruction.SetFlags)
                        cpu.SetLogicalFlags(cpu.Registers[instruction.Destination], orUpdatesCarry, orCarry);
                    break;
                case IrOp.BitClearConstant:
                    cpu.Registers[instruction.Destination] = cpu.Registers[instruction.Source] & ~(uint)instruction.Value;
                    ApplyImmediateLogicalFlags(instruction, cpu);
                    break;
                case IrOp.BitClearRegister:
                    var bicOperand = ShiftOperand(instruction, cpu, out var bicUpdatesCarry, out var bicCarry);
                    cpu.Registers[instruction.Destination] = cpu.Registers[instruction.Source] & ~bicOperand;
                    if (instruction.SetFlags)
                        cpu.SetLogicalFlags(cpu.Registers[instruction.Destination], bicUpdatesCarry, bicCarry);
                    break;
                case IrOp.AddConstant:
                    cpu.Registers[instruction.Destination] = cpu.Add(cpu.Registers[instruction.Source],
                        (uint)instruction.Value, instruction.SetFlags);
                    break;
                case IrOp.AddRegister:
                    cpu.Registers[instruction.Destination] = cpu.Add(cpu.Registers[instruction.Source],
                        ShiftOperand(instruction, cpu, out _, out _), instruction.SetFlags);
                    break;
                case IrOp.SubtractConstant:
                    cpu.Registers[instruction.Destination] = cpu.Subtract(cpu.Registers[instruction.Source],
                        (uint)instruction.Value, instruction.SetFlags);
                    break;
                case IrOp.SubtractRegister:
                    cpu.Registers[instruction.Destination] = cpu.Subtract(cpu.Registers[instruction.Source],
                        ShiftOperand(instruction, cpu, out _, out _), instruction.SetFlags);
                    break;
                case IrOp.CompareConstant:
                    cpu.Compare(cpu.Registers[instruction.Source], (uint)instruction.Value);
                    break;
                case IrOp.CompareRegister:
                    cpu.Compare(cpu.Registers[instruction.Source], ShiftOperand(instruction, cpu, out _, out _));
                    break;
                case IrOp.TestConstant:
                    var testConstantResult = cpu.Registers[instruction.Source] & (uint)instruction.Value;
                    cpu.SetLogicalFlags(testConstantResult, instruction.UpdateCarry, instruction.CarryValue);
                    break;
                case IrOp.TestRegister:
                    var testOperand = ShiftOperand(instruction, cpu, out var testUpdatesCarry, out var testCarry);
                    cpu.SetLogicalFlags(cpu.Registers[instruction.Source] & testOperand, testUpdatesCarry, testCarry);
                    break;
                case IrOp.Load32:
                    cpu.Registers[instruction.Destination] = memory.Load32(cpu.Registers[instruction.Source] + (uint)instruction.Value);
                    break;
                case IrOp.Store32:
                    var baseRegister = (int)(instruction.Value >> 32);
                    memory.Store32(cpu.Registers[baseRegister] + (uint)instruction.Value, cpu.Registers[instruction.Source]);
                    break;
                case IrOp.Load32PreWriteback:
                    var load32PreAddress = cpu.Registers[instruction.Source] + (uint)instruction.Value;
                    cpu.Registers[instruction.Destination] = memory.Load32(load32PreAddress);
                    cpu.Registers[instruction.Source] = load32PreAddress;
                    break;
                case IrOp.Store32PreWriteback:
                    var store32PreBase = (int)(instruction.Value >> 32);
                    var store32PreAddress = cpu.Registers[store32PreBase] + (uint)instruction.Value;
                    memory.Store32(store32PreAddress, cpu.Registers[instruction.Source]);
                    cpu.Registers[store32PreBase] = store32PreAddress;
                    break;
                case IrOp.Load32PostWriteback:
                    var load32PostAddress = cpu.Registers[instruction.Source];
                    cpu.Registers[instruction.Destination] = memory.Load32(load32PostAddress);
                    cpu.Registers[instruction.Source] = load32PostAddress + (uint)instruction.Value;
                    break;
                case IrOp.Store32PostWriteback:
                    var store32PostBase = (int)(instruction.Value >> 32);
                    var store32PostAddress = cpu.Registers[store32PostBase];
                    memory.Store32(store32PostAddress, cpu.Registers[instruction.Source]);
                    cpu.Registers[store32PostBase] = store32PostAddress + (uint)instruction.Value;
                    break;
                case IrOp.Load8:
                    cpu.Registers[instruction.Destination] = memory.Load8(cpu.Registers[instruction.Source] + (uint)instruction.Value);
                    break;
                case IrOp.Store8:
                    var byteBaseRegister = (int)(instruction.Value >> 32);
                    memory.Store8(cpu.Registers[byteBaseRegister] + (uint)instruction.Value,
                        (byte)cpu.Registers[instruction.Source]);
                    break;
                case IrOp.Load16:
                    cpu.Registers[instruction.Destination] = memory.Load16(
                        cpu.Registers[instruction.Source] + (uint)instruction.Value);
                    break;
                case IrOp.Store16:
                    var halfBaseRegister = (int)(instruction.Value >> 32);
                    memory.Store16(cpu.Registers[halfBaseRegister] + (uint)instruction.Value,
                        (ushort)cpu.Registers[instruction.Source]);
                    break;
                case IrOp.LoadSigned8:
                    cpu.Registers[instruction.Destination] = memory.LoadSigned8(
                        cpu.Registers[instruction.Source] + (uint)instruction.Value);
                    break;
                case IrOp.LoadSigned16:
                    cpu.Registers[instruction.Destination] = memory.LoadSigned16(
                        cpu.Registers[instruction.Source] + (uint)instruction.Value);
                    break;
                case IrOp.Load8PreWriteback:
                    var load8PreAddress = cpu.Registers[instruction.Source] + (uint)instruction.Value;
                    cpu.Registers[instruction.Destination] = memory.Load8(load8PreAddress);
                    cpu.Registers[instruction.Source] = load8PreAddress;
                    break;
                case IrOp.Store8PreWriteback:
                    var store8PreBase = (int)(instruction.Value >> 32);
                    var store8PreAddress = cpu.Registers[store8PreBase] + (uint)instruction.Value;
                    memory.Store8(store8PreAddress, (byte)cpu.Registers[instruction.Source]);
                    cpu.Registers[store8PreBase] = store8PreAddress;
                    break;
                case IrOp.Load8PostWriteback:
                    var load8PostAddress = cpu.Registers[instruction.Source];
                    cpu.Registers[instruction.Destination] = memory.Load8(load8PostAddress);
                    cpu.Registers[instruction.Source] = load8PostAddress + (uint)instruction.Value;
                    break;
                case IrOp.Store8PostWriteback:
                    var store8PostBase = (int)(instruction.Value >> 32);
                    var store8PostAddress = cpu.Registers[store8PostBase];
                    memory.Store8(store8PostAddress, (byte)cpu.Registers[instruction.Source]);
                    cpu.Registers[store8PostBase] = store8PostAddress + (uint)instruction.Value;
                    break;
                case IrOp.LoadMultiple:
                    var loadedPc = TransferMultiple(cpu, memory, instruction.Value, load: true);
                    if (loadedPc is { } multiplePc) return new(HaltReason.JumpRegister, multiplePc);
                    break;
                case IrOp.StoreMultiple:
                    TransferMultiple(cpu, memory, instruction.Value, load: false);
                    break;
                case IrOp.Push:
                    Push(cpu, memory, (ushort)instruction.Value);
                    break;
                case IrOp.Pop:
                    var poppedPc = Pop(cpu, memory, (ushort)instruction.Value);
                    if (poppedPc is { } pc) return new(HaltReason.JumpRegister, pc);
                    break;
                case IrOp.Jump:
                    return new(HaltReason.Jump, (uint)instruction.Value);
                case IrOp.Call:
                    cpu.Registers[14] = instruction.GuestAddress + 4;
                    return new(HaltReason.Call, (uint)instruction.Value);
                case IrOp.CallRegister:
                    cpu.Registers[14] = instruction.GuestAddress + 4;
                    return new(HaltReason.Call, cpu.Registers[instruction.Source]);
                case IrOp.JumpRegister:
                    return new(HaltReason.JumpRegister, cpu.Registers[instruction.Source]);
                case IrOp.SupervisorCall:
                    return new(HaltReason.SupervisorCall, ServiceId: (uint)instruction.Value);
                case IrOp.ReadThreadPointer:
                    cpu.Registers[instruction.Destination] = cpu.ThreadPointer;
                    break;
                case IrOp.Unsupported:
                    throw new NotSupportedException($"Unsupported instruction 0x{instruction.RawInstruction:X8} at 0x{instruction.GuestAddress:X8}.");
                case IrOp.Nop:
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(instruction), instruction.Op, "Unknown IR operation.");
            }
        }
        return new(HaltReason.EndOfBlock);
    }

    private static void ApplyImmediateLogicalFlags(IrInstruction instruction, ReferenceCpu cpu)
    {
        if (instruction.SetFlags)
            cpu.SetLogicalFlags(cpu.Registers[instruction.Destination], instruction.UpdateCarry, instruction.CarryValue);
    }

    private static uint ShiftOperand(IrInstruction instruction, ReferenceCpu cpu,
        out bool updateCarry, out bool carry)
    {
        var value = cpu.Registers[(int)instruction.Value];
        var amount = instruction.ShiftAmount;
        updateCarry = true;
        switch (instruction.ShiftType)
        {
            case ArmShiftType.Lsl when amount == 0:
                updateCarry = false;
                carry = cpu.Carry;
                return value;
            case ArmShiftType.Lsl:
                carry = ((value >> (32 - amount)) & 1) != 0;
                return value << amount;
            case ArmShiftType.Lsr:
                if (amount == 0)
                {
                    carry = (value & 0x8000_0000u) != 0;
                    return 0;
                }
                carry = ((value >> (amount - 1)) & 1) != 0;
                return value >> amount;
            case ArmShiftType.Asr:
                if (amount == 0)
                {
                    carry = (value & 0x8000_0000u) != 0;
                    return carry ? uint.MaxValue : 0;
                }
                carry = ((value >> (amount - 1)) & 1) != 0;
                return unchecked((uint)((int)value >> amount));
            case ArmShiftType.Ror when amount == 0:
                carry = (value & 1) != 0;
                return (cpu.Carry ? 0x8000_0000u : 0) | (value >> 1);
            case ArmShiftType.Ror:
                var result = System.Numerics.BitOperations.RotateRight(value, amount);
                carry = (result & 0x8000_0000u) != 0;
                return result;
            default:
                throw new ArgumentOutOfRangeException(nameof(instruction), instruction.ShiftType, "Unknown shift type.");
        }
    }

    private static void Push(ReferenceCpu cpu, GuestMemory memory, ushort registerList)
    {
        var count = System.Numerics.BitOperations.PopCount(registerList);
        var address = cpu.Registers[13] - (uint)(count * 4);
        cpu.Registers[13] = address;
        for (var register = 0; register < 16; register++)
        {
            if ((registerList & (1 << register)) == 0) continue;
            memory.Store32(address, cpu.Registers[register]);
            address += 4;
        }
    }

    private static uint? TransferMultiple(ReferenceCpu cpu, GuestMemory memory, ulong descriptor, bool load)
    {
        var registerList = (ushort)descriptor;
        var baseRegister = (int)((descriptor >> 16) & 0xF);
        var preIndex = (descriptor & (1UL << 20)) != 0;
        var increment = (descriptor & (1UL << 21)) != 0;
        var writeBack = (descriptor & (1UL << 22)) != 0;
        var count = System.Numerics.BitOperations.PopCount(registerList);
        var baseAddress = cpu.Registers[baseRegister];
        var byteCount = (uint)(count * 4);
        var address = increment
            ? baseAddress + (preIndex ? 4u : 0u)
            : baseAddress - byteCount + (preIndex ? 0u : 4u);
        uint? pc = null;
        for (var register = 0; register < 16; register++)
        {
            if ((registerList & (1 << register)) == 0) continue;
            if (load)
            {
                var value = memory.Load32(address);
                if (register == 15) pc = value;
                else cpu.Registers[register] = value;
            }
            else
            {
                memory.Store32(address, cpu.Registers[register]);
            }
            address += 4;
        }
        if (writeBack) cpu.Registers[baseRegister] = increment ? baseAddress + byteCount : baseAddress - byteCount;
        return pc;
    }

    private static uint? Pop(ReferenceCpu cpu, GuestMemory memory, ushort registerList)
    {
        var address = cpu.Registers[13];
        uint? pc = null;
        for (var register = 0; register < 16; register++)
        {
            if ((registerList & (1 << register)) == 0) continue;
            var value = memory.Load32(address);
            address += 4;
            if (register == 15) pc = value;
            else cpu.Registers[register] = value;
        }
        cpu.Registers[13] = address;
        return pc;
    }
}
