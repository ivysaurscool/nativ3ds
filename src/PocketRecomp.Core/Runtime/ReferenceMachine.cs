using System.Buffers.Binary;
using PocketRecomp.Core.Arm;
using PocketRecomp.Core.Ir;

namespace PocketRecomp.Core.Runtime;

public enum HaltReason { EndOfBlock, Jump, Call, JumpRegister, SupervisorCall }

public sealed record ExecutionResult(HaltReason Reason, uint? Target = null, uint? ServiceId = null);

public sealed class ReferenceCpu
{
    public uint[] Registers { get; } = new uint[16];
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

    public void Store32(uint address, uint value)
    {
        var offset = GetOffset(address, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, 4), value);
    }

    private int GetOffset(uint address, int size)
    {
        if (address < BaseAddress) throw new InvalidOperationException($"Guest address 0x{address:X8} is below mapped memory.");
        var offset = (ulong)address - BaseAddress;
        if (offset + (uint)size > (ulong)data.Length)
            throw new InvalidOperationException($"Guest address 0x{address:X8} is outside mapped memory.");
        if ((address & 3) != 0) throw new InvalidOperationException($"Unaligned 32-bit guest access at 0x{address:X8}.");
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
                    break;
                case IrOp.AddConstant:
                    cpu.Registers[instruction.Destination] = cpu.Registers[instruction.Source] + (uint)instruction.Value;
                    break;
                case IrOp.SubtractConstant:
                    cpu.Registers[instruction.Destination] = cpu.Registers[instruction.Source] - (uint)instruction.Value;
                    break;
                case IrOp.CompareConstant:
                    cpu.Compare(cpu.Registers[instruction.Source], (uint)instruction.Value);
                    break;
                case IrOp.Load32:
                    cpu.Registers[instruction.Destination] = memory.Load32(cpu.Registers[instruction.Source] + (uint)instruction.Value);
                    break;
                case IrOp.Store32:
                    var baseRegister = (int)(instruction.Value >> 32);
                    memory.Store32(cpu.Registers[baseRegister] + (uint)instruction.Value, cpu.Registers[instruction.Source]);
                    break;
                case IrOp.Jump:
                    return new(HaltReason.Jump, (uint)instruction.Value);
                case IrOp.Call:
                    cpu.Registers[14] = instruction.GuestAddress + 4;
                    return new(HaltReason.Call, (uint)instruction.Value);
                case IrOp.JumpRegister:
                    return new(HaltReason.JumpRegister, cpu.Registers[instruction.Source]);
                case IrOp.SupervisorCall:
                    return new(HaltReason.SupervisorCall, ServiceId: (uint)instruction.Value);
                case IrOp.Unsupported:
                    throw new NotSupportedException($"Unsupported instruction 0x{instruction.RawInstruction:X8} at 0x{instruction.GuestAddress:X8}.");
                default:
                    throw new ArgumentOutOfRangeException(nameof(instruction), instruction.Op, "Unknown IR operation.");
            }
        }
        return new(HaltReason.EndOfBlock);
    }
}
