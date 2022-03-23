//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.State;

namespace Nethermind.Evm;

[StructLayout(LayoutKind.Explicit, Size = Size)]
struct Word
{
    public const int Size = 32;

    [FieldOffset(0)] public unsafe fixed byte _buffer[Size];

    [FieldOffset(Size - sizeof(byte))]
    public byte Byte0;

    [FieldOffset(Size - sizeof(int))]
    public int Int0;

    [FieldOffset(Size - sizeof(int))]
    public uint UInt0;

    [FieldOffset(Size - 2 * sizeof(int))]
    public uint UInt1;

    [FieldOffset(Size - 1 * sizeof(ulong))]
    public ulong Ulong0;

    [FieldOffset(Size - 2 * sizeof(ulong))]
    public ulong Ulong1;

    [FieldOffset(Size - 3 * sizeof(ulong))]
    public ulong Ulong2;

    [FieldOffset(Size - 4 * sizeof(ulong))]
    public ulong Ulong3;

    public static readonly FieldInfo Byte0Field = typeof(Word).GetField(nameof(Byte0));

    public static readonly FieldInfo Int0Field = typeof(Word).GetField(nameof(Int0));

    public static readonly FieldInfo UInt0Field = typeof(Word).GetField(nameof(UInt0));
    public static readonly FieldInfo UInt1Field = typeof(Word).GetField(nameof(UInt1));

    public static readonly FieldInfo Ulong0Field = typeof(Word).GetField(nameof(Ulong0));
    public static readonly FieldInfo Ulong1Field = typeof(Word).GetField(nameof(Ulong1));
    public static readonly FieldInfo Ulong2Field = typeof(Word).GetField(nameof(Ulong2));
    public static readonly FieldInfo Ulong3Field = typeof(Word).GetField(nameof(Ulong3));

    public static readonly MethodInfo GetIsZero = typeof(Word).GetProperty(nameof(IsZero))!.GetMethod;

    public static readonly MethodInfo GetUInt256 = typeof(Word).GetProperty(nameof(UInt256))!.GetMethod;
    public static readonly MethodInfo SetUInt256 = typeof(Word).GetProperty(nameof(UInt256))!.SetMethod;

    public bool IsZero => (Ulong0 | Ulong1 | Ulong2 | Ulong3) == 0;

    public UInt256 UInt256
    {
        get
        {
            ulong u3 = Ulong3;
            ulong u2 = Ulong2;
            ulong u1 = Ulong1;
            ulong u0 = Ulong0;

            if (BitConverter.IsLittleEndian)
            {
                u3 = BinaryPrimitives.ReverseEndianness(u3);
                u2 = BinaryPrimitives.ReverseEndianness(u2);
                u1 = BinaryPrimitives.ReverseEndianness(u1);
                u0 = BinaryPrimitives.ReverseEndianness(u0);
            }

            return new UInt256(u0, u1, u2, u3);
        }
        set
        {
            ulong u3 = value.u3;
            ulong u2 = value.u2;
            ulong u1 = value.u1;
            ulong u0 = value.u0;

            if (BitConverter.IsLittleEndian)
            {
                u3 = BinaryPrimitives.ReverseEndianness(u3);
                u2 = BinaryPrimitives.ReverseEndianness(u2);
                u1 = BinaryPrimitives.ReverseEndianness(u1);
                u0 = BinaryPrimitives.ReverseEndianness(u0);
            }

            Ulong3 = u3;
            Ulong2 = u2;
            Ulong1 = u1;
            Ulong0 = u0;
        }
    }
}

public class IlVirtualMachine : IVirtualMachine
{
    public TransactionSubstate Run(EvmState state, IWorldState worldState, ITxTracer tracer)
    {
        byte[] code = state.Env.CodeInfo.MachineCode;

        Dictionary<int, long> gasCost = BuildCostLookup(code);

        // TODO: stack invariants, gasCost application

        DynamicMethod method = new("JIT_" + state.Env.CodeSource, typeof(EvmExceptionType), Type.EmptyTypes, typeof(IlVirtualMachine).Assembly.Modules.First(), true)
        {
            InitLocals = false
        };

        ILGenerator il = method.GetILGenerator();

        LocalBuilder jmpDestination = il.DeclareLocal(Word.Int0Field.FieldType);
        LocalBuilder consumeJumpCondition = il.DeclareLocal(typeof(int));
        LocalBuilder uint256a = il.DeclareLocal(typeof(UInt256));
        LocalBuilder uint256b = il.DeclareLocal(typeof(UInt256));
        LocalBuilder uint256c = il.DeclareLocal(typeof(UInt256));

        // TODO: stack check for head
        LocalBuilder stack = il.DeclareLocal(typeof(Word*));
        LocalBuilder current = il.DeclareLocal(typeof(Word*));

        const int wordToAlignTo = 32;

        il.Emit(OpCodes.Ldc_I4, EvmStack.MaxStackSize * Word.Size + wordToAlignTo);
        il.Emit(OpCodes.Localloc);

        // align to the boundary, so that the Word can be written using the aligned longs.
        il.LoadValue(wordToAlignTo);
        il.Emit(OpCodes.Conv_I);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Ldc_I4, ~(wordToAlignTo - 1));
        il.Emit(OpCodes.Conv_I);
        il.Emit(OpCodes.And);

        il.Store(stack); // store as start

        il.Load(stack);
        il.Store(current); // copy to the current

        int pc = 0;

        Label ret = il.DefineLabel(); // the label just before return
        Label invalidAddress = il.DefineLabel(); // invalid jump address
        Label jumpTable = il.DefineLabel(); // jump table

        Dictionary<int, Label> jumpDestinations = new();

        for (int i = 0; i < code.Length; i++)
        {
            Operation op = _operations[(Instruction)code[i]];

            switch (op.Instruction)
            {
                case Instruction.PC:
                    il.CleanWord(current);
                    il.Load(current);
                    il.LoadValue(BinaryPrimitives.ReverseEndianness(pc)); // TODO: assumes little endian machine
                    il.Emit(OpCodes.Stfld, Word.UInt0Field);
                    il.StackPush(current);
                    break;

                // pushes work as follows
                // 1. load the next top pointer of the stack
                // 2. zero it
                // 3. load the value
                // 4. set the field
                // 5. advance pointer
                case Instruction.PUSH1:
                    il.Load(current);
                    il.Emit(OpCodes.Initobj, typeof(Word));
                    il.Load(current);
                    byte push1 = (byte)((i + 1 >= code.Length) ? 0 : code[i + 1]);
                    il.Emit(OpCodes.Ldc_I4_S, push1);
                    il.Emit(OpCodes.Stfld, Word.Byte0Field);
                    il.StackPush(current);
                    break;
                case Instruction.PUSH2:
                case Instruction.PUSH3:
                case Instruction.PUSH4:
                    il.Load(current);
                    il.Emit(OpCodes.Initobj, typeof(Word));
                    il.Load(current);
                    int push2 = BinaryPrimitives.ReadInt32LittleEndian(code.Slice(i + 1).ToArray().Concat(new byte[4]).ToArray());
                    il.Emit(OpCodes.Ldc_I4, BinaryPrimitives.ReverseEndianness(push2));
                    il.Emit(OpCodes.Stfld, Word.Int0Field);
                    il.StackPush(current);
                    break;
                case Instruction.DUP1:
                    il.Load(current);
                    il.StackLoadPrevious(current, 1 + op.Instruction - Instruction.DUP1);
                    il.Emit(OpCodes.Ldobj, typeof(Word));
                    il.Emit(OpCodes.Stobj, typeof(Word));
                    il.StackPush(current);
                    break;
                case Instruction.POP:
                    il.StackPop(current);
                    break;
                case Instruction.JUMPDEST:
                    Label dest = il.DefineLabel();
                    jumpDestinations[pc] = dest;
                    il.MarkLabel(dest);
                    break;
                case Instruction.JUMP:
                    il.Emit(OpCodes.Br, jumpTable);
                    break;
                case Instruction.JUMPI:
                    Label noJump = il.DefineLabel();

                    il.StackLoadPrevious(current, 2); // load condition that is on the second
                    il.EmitCall(OpCodes.Call, Word.GetIsZero, null);

                    il.Emit(OpCodes.Brtrue_S, noJump); // if zero, just jump to removal two values and move on

                    // condition is met, mark condition as to be removed
                    il.LoadValue(1);
                    il.Store(consumeJumpCondition);
                    il.Emit(OpCodes.Br, jumpTable);

                    // condition is not met, just consume
                    il.MarkLabel(noJump);
                    il.StackPop(current, 2);
                    break;
                case Instruction.SUB:
                    // a
                    il.StackLoadPrevious(current, 1);
                    il.EmitCall(OpCodes.Call, Word.GetUInt256, null);
                    il.Store(uint256a);

                    // b
                    il.StackLoadPrevious(current, 2);
                    il.EmitCall(OpCodes.Call, Word.GetUInt256, null); // stack: uint256, uint256
                    il.Store(uint256b);

                    // a - b = c
                    il.LoadAddress(uint256a);
                    il.LoadAddress(uint256b);
                    il.LoadAddress(uint256c);

                    MethodInfo subtract = typeof(UInt256).GetMethod(nameof(UInt256.Subtract), BindingFlags.Public | BindingFlags.Static)!;
                    il.EmitCall(OpCodes.Call, subtract, null); // stack: _

                    il.StackPop(current);
                    il.Load(current);
                    il.Load(uint256c); // stack: word*, uint256
                    il.EmitCall(OpCodes.Call, Word.SetUInt256, null);
                    break;
                default:
                    throw new NotImplementedException();
            }

            i += op.AdditionalBytes;
            pc++;
        }

        // jump to return
        il.LoadValue((int)EvmExceptionType.None);
        il.Emit(OpCodes.Br, ret);

        // jump table
        il.MarkLabel(jumpTable);

        il.StackPop(current); // move the stack down to address

        // if (jumpDest > uint.MaxValue)
        // ULong3 | Ulong2 | Ulong1 | Uint1 | Ushort1
        il.Load(current, Word.Ulong3Field);
        il.Load(current, Word.Ulong2Field);
        il.Emit(OpCodes.Or);
        il.Load(current, Word.Ulong1Field);
        il.Emit(OpCodes.Or);
        il.Load(current, Word.UInt1Field);
        il.Emit(OpCodes.Conv_U8);
        il.Emit(OpCodes.Or);

        il.Emit(OpCodes.Brtrue, invalidAddress);

        // emit actual jump table with first switch statement covering fanout of values, then ifs in specific branches
        const int jumpFanOutLog = 7; // 128
        const int bitMask = (1 << jumpFanOutLog) - 1;

        Label[] jumps = new Label[jumpFanOutLog];
        for (int i = 0; i < jumpFanOutLog; i++)
        {
            jumps[i] = il.DefineLabel();
        }

        // save to helper
        il.Load(current, Word.Int0Field);

        // endianess!
        il.EmitCall(OpCodes.Call, typeof(BinaryPrimitives).GetMethod(nameof(BinaryPrimitives.ReverseEndianness), BindingFlags.Public | BindingFlags.Static, new[] { typeof(uint) }), null);
        il.Store(jmpDestination);

        // consume if this was a conditional jump and zero it. Notice that this is a branch-free approach that uses 0 or 1 + multiplication to advance the word pointer or not
        il.StackPop(current, consumeJumpCondition);
        il.LoadValue(0);
        il.Store(consumeJumpCondition);

        // & with mask
        il.Load(jmpDestination);
        il.LoadValue(bitMask);
        il.Emit(OpCodes.And);

        il.Emit(OpCodes.Switch, jumps); // actual jump table to jump directly to the specific range of addresses

        int[] destinations = jumpDestinations.Keys.ToArray();

        for (int i = 0; i < jumpFanOutLog; i++)
        {
            il.MarkLabel(jumps[i]);

            // for each destination matching the bit mask emit check for the equality
            foreach (int dest in destinations.Where(dest => (dest & bitMask) == i))
            {
                il.Load(jmpDestination);
                il.LoadValue(dest);
                il.Emit(OpCodes.Beq, jumpDestinations[dest]);
            }

            // each bucket ends with a jump to invalid access to do not fall through to another one
            il.Emit(OpCodes.Br, invalidAddress);
        }

        // invalid address return
        il.MarkLabel(invalidAddress);
        il.LoadValue((int)EvmExceptionType.InvalidJumpDestination);
        il.Emit(OpCodes.Br, ret);

        // return
        il.MarkLabel(ret);
        il.Emit(OpCodes.Ret);

        Func<EvmExceptionType> del = method.CreateDelegate<Func<EvmExceptionType>>();

        return new TransactionSubstate(del(), true);
    }

    public void DisableSimdInstructions()
    {
        throw new NotImplementedException();
    }

    private static Dictionary<int, long> BuildCostLookup(ReadOnlySpan<byte> code)
    {
        Dictionary<int, long> costs = new();
        int costStart = 0;
        long costCurrent = 0;

        for (int i = 0; i < code.Length; i++)
        {
            Operation op = _operations[(Instruction)code[i]];
            if (op.GasComputationPoint)
            {
                costs[costStart] = costCurrent;
                costStart = i;
                costCurrent = 0;
            }

            costCurrent += op.GasCost;
            i += op.AdditionalBytes;
        }

        if (costCurrent > 0)
        {
            costs[costStart] = costCurrent;
        }

        return costs;
    }

    private static readonly IReadOnlyDictionary<Instruction, Operation> _operations =
        new Operation[]
        {
            new(Instruction.POP, GasCostOf.Base, 0, 1, 0),
            new(Instruction.PC, GasCostOf.Base, 0, 0, 1),
            new(Instruction.PUSH1, GasCostOf.VeryLow, 1, 0, 1),
            new(Instruction.PUSH2, GasCostOf.VeryLow, 2, 0, 1),
            new(Instruction.PUSH3, GasCostOf.VeryLow, 3, 0, 1),
            new(Instruction.PUSH4, GasCostOf.VeryLow, 4, 0, 1),
            new(Instruction.JUMPDEST, GasCostOf.JumpDest, 0, 0, 0, true),
            new(Instruction.JUMP, GasCostOf.Mid, 0, 1, 0, true),
            new(Instruction.JUMPI, GasCostOf.High, 0, 2, 0, true),
            new(Instruction.SUB, GasCostOf.VeryLow, 0, 2, 1),
            new(Instruction.DUP1, GasCostOf.VeryLow, 0, 1, 2)
        }.ToDictionary(op => op.Instruction);
    
    public readonly struct Operation
    {
        /// <summary>
        /// The actual instruction
        /// </summary>
        public Instruction Instruction { get; }

        /// <summary>
        /// The gas cost.
        /// </summary>
        public long GasCost { get; }

        /// <summary>
        /// How many following bytes does this instruction have.
        /// </summary>
        public byte AdditionalBytes { get; }

        /// <summary>
        /// How many bytes are popped by this instruction.
        /// </summary>
        public byte StackBehaviorPop { get; }

        /// <summary>
        /// How many bytes are pushed by this instruction.
        /// </summary>
        public byte StackBehaviorPush { get; }

        /// <summary>
        /// Marks the point important from the gas computation point
        /// </summary>
        public bool GasComputationPoint { get; }

        /// <summary>
        /// Creates the new operation.
        /// </summary>
        public Operation(Instruction instruction, long gasCost, byte additionalBytes, byte stackBehaviorPop, byte stackBehaviorPush, bool gasComputationPoint = false)
        {
            Instruction = instruction;
            GasCost = gasCost;
            AdditionalBytes = additionalBytes;
            StackBehaviorPop = stackBehaviorPop;
            StackBehaviorPush = stackBehaviorPush;
            GasComputationPoint = gasComputationPoint;
        }
    }
}

static class EmitExtensions
{
    public static void Load(this ILGenerator il, LocalBuilder local)
    {
        switch (local.LocalIndex)
        {
            case 0:
                il.Emit(OpCodes.Ldloc_0);
                break;
            case 1:
                il.Emit(OpCodes.Ldloc_1);
                break;
            case 2:
                il.Emit(OpCodes.Ldloc_2);
                break;
            case 3:
                il.Emit(OpCodes.Ldloc_3);
                break;
            default:
                if (local.LocalIndex < 255)
                {
                    il.Emit(OpCodes.Ldloc_S, (byte)local.LocalIndex);
                }
                else
                {
                    il.Emit(OpCodes.Ldloc, local.LocalIndex);
                }
                break;
        }
    }

    public static void LoadAddress(this ILGenerator il, LocalBuilder local)
    {
        if (local.LocalIndex <= 255)
        {
            il.Emit(OpCodes.Ldloca_S, (byte)local.LocalIndex);
        }
        else
        {
            il.Emit(OpCodes.Ldloca, local.LocalIndex);
        }
    }

    public static void Load(this ILGenerator il, LocalBuilder local, FieldInfo wordField)
    {
        if (local.LocalType != typeof(Word*))
        {
            throw new ArgumentException($"Only Word* can be used. This variable is of type {local.LocalType}");
        }

        if (wordField.DeclaringType != typeof(Word))
        {
            throw new ArgumentException($"Only Word fields can be used. This field is declared for {wordField.DeclaringType}");
        }

        il.Load(local);
        il.Emit(OpCodes.Ldfld, wordField);
    }

    public static void CleanWord(this ILGenerator il, LocalBuilder local)
    {
        if (local.LocalType != typeof(Word*))
        {
            throw new ArgumentException(
                $"Only {nameof(Word)} pointers are supported. The passed local was type of {local.LocalType}.");
        }

        il.Load(local);
        il.Emit(OpCodes.Initobj, typeof(Word));
    }

    /// <summary>
    /// Advances the stack one word up.
    /// </summary>
    public static void StackPush(this ILGenerator il, LocalBuilder local)
    {
        il.Load(local);
        il.LoadValue(Word.Size);
        il.Emit(OpCodes.Conv_I);
        il.Emit(OpCodes.Add);
        il.Store(local);
    }

    /// <summary>
    /// Moves the stack <paramref name="count"/> words down.
    /// </summary>
    public static void StackPop(this ILGenerator il, LocalBuilder local, int count = 1)
    {
        il.Load(local);
        il.LoadValue(Word.Size * count);
        il.Emit(OpCodes.Conv_I);
        il.Emit(OpCodes.Sub);
        il.Store(local);
    }

    /// <summary>
    /// Moves the stack <paramref name="count"/> words down.
    /// </summary>
    public static void StackPop(this ILGenerator il, LocalBuilder local, LocalBuilder count)
    {
        il.Load(local);
        il.LoadValue(Word.Size);
        il.Load(count);
        il.Emit(OpCodes.Mul);
        il.Emit(OpCodes.Conv_I);
        il.Emit(OpCodes.Sub);
        il.Store(local);
    }

    /// <summary>
    /// Loads the previous EVM stack value on top of .NET stack.
    /// </summary>
    public static void StackLoadPrevious(this ILGenerator il, LocalBuilder local, int count = 1)
    {
        il.Load(local);
        il.LoadValue(Word.Size * count);
        il.Emit(OpCodes.Conv_I);
        il.Emit(OpCodes.Sub);
    }

    public static void Store(this ILGenerator il, LocalBuilder local)
    {
        switch (local.LocalIndex)
        {
            case 0:
                il.Emit(OpCodes.Stloc_0);
                break;
            case 1:
                il.Emit(OpCodes.Stloc_1);
                break;
            case 2:
                il.Emit(OpCodes.Stloc_2);
                break;
            case 3:
                il.Emit(OpCodes.Stloc_3);
                break;
            default:
                if (local.LocalIndex < 255)
                {
                    il.Emit(OpCodes.Stloc_S, (byte)local.LocalIndex);
                }
                else
                {
                    il.Emit(OpCodes.Stloc, local.LocalIndex);
                }
                break;
        }
    }

    public static void LoadValue(this ILGenerator il, int value)
    {
        switch (value)
        {
            case 0:
                il.Emit(OpCodes.Ldc_I4_0);
                break;
            case 1:
                il.Emit(OpCodes.Ldc_I4_1);
                break;
            case 2:
                il.Emit(OpCodes.Ldc_I4_2);
                break;
            case 3:
                il.Emit(OpCodes.Ldc_I4_3);
                break;
            case 4:
                il.Emit(OpCodes.Ldc_I4_4);
                break;
            case 5:
                il.Emit(OpCodes.Ldc_I4_5);
                break;
            case 6:
                il.Emit(OpCodes.Ldc_I4_6);
                break;
            case 7:
                il.Emit(OpCodes.Ldc_I4_7);
                break;
            case 8:
                il.Emit(OpCodes.Ldc_I4_8);
                break;
            default:
                if (value <= 255)
                    il.Emit(OpCodes.Ldc_I4_S, (byte)value);
                else
                    il.Emit(OpCodes.Ldc_I4, value);
                break;
        }
    }
}
