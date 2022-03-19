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
using Nethermind.Evm.Tracing;
using Nethermind.State;

namespace Nethermind.Evm;

[StructLayout(LayoutKind.Explicit, Size = Size)]
struct Word
{
    public const int Size = 32;

    [FieldOffset(0)] public unsafe fixed byte _buffer[Size];

    [FieldOffset(Size - sizeof(int))]
    public int Int0;

    [FieldOffset(Size - sizeof(byte))]
    public byte Byte0;

    public static readonly FieldInfo Int0Field = typeof(Word).GetField(nameof(Int0));

    public static readonly FieldInfo Byte0Field = typeof(Word).GetField(nameof(Byte0));
}

public class IlVirtualMachine : IVirtualMachine
{
    public TransactionSubstate Run(EvmState state, IWorldState worldState, ITxTracer tracer)
    {
        byte[] code = state.Env.CodeInfo.MachineCode;

        Dictionary<int, long> gasCost = BuildCostLookup(code);

        // TODO: stack invariants, gasCost application

        DynamicMethod method = new("JIT_" + state.Env.CodeSource, typeof(void), Type.EmptyTypes, typeof(IlVirtualMachine).Assembly.Modules.First(), true)
        {
            InitLocals = false
        };

        ILGenerator il = method.GetILGenerator();

        // TODO: stack check
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

        for (int i = 0; i < code.Length; i++)
        {
            Operation op = _operations[(Instruction)code[i]];

            switch (op.Instruction)
            {
                case Instruction.PC:
                    il.CleanWord(current);
                    il.Load(current);
                    il.LoadValue(BinaryPrimitives.ReverseEndianness(pc)); // TODO: assumes little endian machine
                    il.Emit(OpCodes.Stfld, Word.Int0Field);
                    il.StackUp(current);
                    break;
                case Instruction.PUSH1:
                    il.CleanWord(current);
                    il.Load(current);
                    int value = (i + 1 >= code.Length) ? 0 : code[i + 1];
                    il.LoadValue(value);
                    il.Emit(OpCodes.Stfld, Word.Byte0Field);
                    il.StackUp(current);
                    break;
                case Instruction.POP:
                    il.Load(current);
                    il.LoadValue(Word.Size);
                    il.Emit(OpCodes.Conv_I);
                    il.Emit(OpCodes.Sub);
                    il.Store(current);
                    break;
                default:
                    throw new NotImplementedException();
            }

            i += op.AdditionalBytes;
            pc++;
        }

        il.Emit(OpCodes.Ret);

        Action del = method.CreateDelegate<Action>();
        del();

        return new TransactionSubstate(EvmExceptionType.None, false);
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
        new Dictionary<Instruction, Operation>
        {
            { Instruction.POP, new(Instruction.POP, GasCostOf.Base, 0, 1, 0)},
            { Instruction.PC, new(Instruction.PC, GasCostOf.Base, 0, 0, 1)},
            { Instruction.PUSH1, new(Instruction.PUSH1, GasCostOf.VeryLow, 1, 0, 1)},
            //{ Instruction.JUMPDEST, new(Instruction.JUMPDEST, GasCostOf.JumpDest, 0, 0, 0, true)},
            //{ Instruction.JUMP, new(Instruction.JUMP, GasCostOf.Mid, 0, 1, 0, true)}
        };

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

    public static void StackUp(this ILGenerator il, LocalBuilder local)
    {
        il.Load(local);
        il.LoadValue(Word.Size);
        il.Emit(OpCodes.Conv_I);
        il.Emit(OpCodes.Add);
        il.Store(local);
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
