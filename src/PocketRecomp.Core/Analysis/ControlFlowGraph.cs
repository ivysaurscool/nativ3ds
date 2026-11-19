using System.Collections.ObjectModel;
using System.Buffers.Binary;
using PocketRecomp.Core.Arm;

namespace PocketRecomp.Core.Analysis;

public sealed record BasicBlock(
    uint Address,
    uint EndAddress,
    IReadOnlyList<ArmInstruction> Instructions,
    IReadOnlyList<uint> Successors,
    bool EndsWithUnsupported);

public sealed record ControlFlowGraph(
    uint BaseAddress,
    int CodeSize,
    IReadOnlyDictionary<uint, BasicBlock> Blocks,
    IReadOnlyList<uint> UnresolvedTargets)
{
    public int InstructionCount => Blocks.Values.Sum(block => block.Instructions.Count);
    public int UnsupportedBlockCount => Blocks.Values.Count(block => block.EndsWithUnsupported);
}

/// <summary>Discovers reachable ARM-state blocks from trusted entry points.</summary>
public static class ControlFlowDiscoverer
{
    public static ControlFlowGraph Discover(ReadOnlySpan<byte> code, uint baseAddress,
        IEnumerable<uint> entryPoints, int maxBlocks = 100_000)
    {
        if ((baseAddress & 3) != 0) throw new ArgumentException("ARM base address must be aligned.", nameof(baseAddress));
        if (maxBlocks <= 0) throw new ArgumentOutOfRangeException(nameof(maxBlocks));

        var roots = entryPoints.Distinct().ToArray();
        var leaders = FindLeaders(code, baseAddress, roots);
        var pending = new Queue<uint>(roots);
        var blocks = new SortedDictionary<uint, BasicBlock>();
        var unresolved = new SortedSet<uint>();

        while (pending.Count > 0)
        {
            var address = pending.Dequeue();
            if (blocks.ContainsKey(address)) continue;
            if (!Contains(code, baseAddress, address))
            {
                unresolved.Add(address);
                continue;
            }
            if (blocks.Count >= maxBlocks) throw new InvalidOperationException($"Control-flow graph exceeded {maxBlocks} blocks.");

            var block = DecodeBlock(code, baseAddress, address, leaders);
            blocks.Add(address, block);
            foreach (var successor in block.Successors)
            {
                if (Contains(code, baseAddress, successor)) pending.Enqueue(successor);
                else unresolved.Add(successor);
            }
        }

        return new(baseAddress, code.Length,
            new ReadOnlyDictionary<uint, BasicBlock>(blocks), unresolved.ToArray());
    }

    private static HashSet<uint> FindLeaders(ReadOnlySpan<byte> code, uint baseAddress, IEnumerable<uint> entryPoints)
    {
        var leaders = new HashSet<uint>();
        foreach (var entryPoint in entryPoints)
            if (Contains(code, baseAddress, entryPoint)) leaders.Add(entryPoint);
        for (var offset = 0; offset + 4 <= code.Length; offset += 4)
        {
            var address = baseAddress + (uint)offset;
            var word = BinaryPrimitives.ReadUInt32LittleEndian(code[offset..]);
            var instruction = ArmDecoder.Decode(word, address);
            var next = address + 4;
            if (instruction.Op is ArmOp.Branch or ArmOp.BranchLink)
            {
                if (Contains(code, baseAddress, instruction.Target)) leaders.Add(instruction.Target);
                if (Contains(code, baseAddress, next)) leaders.Add(next);
            }
            else if (instruction.Op is ArmOp.BranchExchange or ArmOp.SoftwareInterrupt)
            {
                if (Contains(code, baseAddress, next)) leaders.Add(next);
            }
        }
        return leaders;
    }

    private static BasicBlock DecodeBlock(ReadOnlySpan<byte> code, uint baseAddress, uint start, HashSet<uint> leaders)
    {
        var instructions = new List<ArmInstruction>();
        var successors = new List<uint>();
        var address = start;
        var unsupported = false;

        while (Contains(code, baseAddress, address))
        {
            if (address != start && leaders.Contains(address))
            {
                successors.Add(address);
                break;
            }

            var offset = checked((int)(address - baseAddress));
            var word = BinaryPrimitives.ReadUInt32LittleEndian(code[offset..]);
            var instruction = ArmDecoder.Decode(word, address);
            instructions.Add(instruction);
            var next = address + 4;

            switch (instruction.Op)
            {
                case ArmOp.Unknown:
                    unsupported = true;
                    address = next;
                    goto Finished;
                case ArmOp.Branch:
                    AddUnique(successors, instruction.Target);
                    if (instruction.Condition != ArmCondition.Al) AddUnique(successors, next);
                    address = next;
                    goto Finished;
                case ArmOp.BranchLink:
                    AddUnique(successors, instruction.Target);
                    AddUnique(successors, next);
                    address = next;
                    goto Finished;
                case ArmOp.BranchExchange:
                    address = next;
                    goto Finished;
                case ArmOp.SoftwareInterrupt:
                    AddUnique(successors, next);
                    address = next;
                    goto Finished;
                default:
                    address = next;
                    break;
            }
        }

    Finished:
        return new(start, address, instructions, successors, unsupported);
    }

    private static bool Contains(ReadOnlySpan<byte> code, uint baseAddress, uint address)
    {
        if ((address & 3) != 0 || address < baseAddress) return false;
        var offset = (ulong)address - baseAddress;
        return offset + 4 <= (ulong)code.Length;
    }

    private static void AddUnique(List<uint> values, uint value)
    {
        if (!values.Contains(value)) values.Add(value);
    }
}
