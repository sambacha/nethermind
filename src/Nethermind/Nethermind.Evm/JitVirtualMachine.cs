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
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using Nethermind.Evm.Tracing;
using Nethermind.State;

namespace Nethermind.Evm;

public class JitVirtualMachine : IVirtualMachine
{
    [StructLayout(LayoutKind.Explicit, Size = Size)]
    struct Word
    {
        public const int Size = 32;

        [FieldOffset(0)]
        private unsafe fixed byte _buffer[Size];
    }

    public TransactionSubstate Run(EvmState state, IWorldState worldState, ITxTracer tracer)
    {
        byte[] code = state.Env.CodeInfo.MachineCode;

        Dictionary<int, long> gasCost = BuildCostLookup(code);

        DynamicMethod method = new("JIT_" + state.Env.CodeSource.ToString(), typeof(void), Type.EmptyTypes, typeof(JitVirtualMachine).Assembly.Modules.First(), true)
        {
            InitLocals = false
        };

        ILGenerator il = method.GetILGenerator();

        // TODO: stack check
        LocalBuilder stack = il.DeclareLocal(typeof(Word*));
        LocalBuilder current = il.DeclareLocal(typeof(Word*));

        il.Emit(OpCodes.Ldc_I4, EvmStack.MaxStackSize * Word.Size);
        il.Emit(OpCodes.Localloc);
        il.Emit(OpCodes.Stloc_0);
        
        il.Emit(OpCodes.Ldloc_0);
        il.Emit(OpCodes.Stloc_1);

        for (int i = 0; i < code.Length; i++)
        {
            Operation op = _operations[(Instruction)code[i]];

            // temporary
            il.Emit(OpCodes.Ldc_I4, (int)op.StackChange);
            il.Emit(OpCodes.Ldloc_1);
            il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Stloc_1);
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
            if (op.FlowControl)
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
            { Instruction.POP, new(Instruction.POP, GasCostOf.Base, 0, -1)},
            { Instruction.PC, new(Instruction.PC, GasCostOf.Base, 0, 1)},
        };

    public readonly struct Operation
    {
        public Instruction Instruction { get; }

        public long GasCost { get; }

        public byte AdditionalBytes { get; }

        public sbyte StackChange { get; }

        public bool FlowControl { get; }

        /// <summary>
        /// Creates the new operation.
        /// </summary>
        /// <param name="instruction">The actual instruction.</param>
        /// <param name="gasCost">The gas cost.</param>
        /// <param name="additionalBytes">The additional bytes the instruction takes beside itself.</param>
        /// <param name="stackChange">The stack change behavior.</param>
        public Operation(Instruction instruction, long gasCost, byte additionalBytes, sbyte stackChange, bool flowControl = false)
        {
            Instruction = instruction;
            GasCost = gasCost;
            AdditionalBytes = additionalBytes;
            StackChange = stackChange;
            FlowControl = flowControl;
        }
    }
}
