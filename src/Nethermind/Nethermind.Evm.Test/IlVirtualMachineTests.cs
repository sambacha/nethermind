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

using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public class IlVirtualMachineTests : VirtualMachineTestsBase
{
    protected override IVirtualMachine BuildVirtualMachine(IBlockhashProvider blockhashProvider, ILogManager logManager) => new IlVirtualMachine();

    [Test]
    public void Test1()
    {
        byte[] code = Prepare.EvmCode
            .Op(Instruction.PC)
            .Op(Instruction.POP)
            .Done;

        TestAllTracerWithOutput result = Execute(code);
    }

    [Test]
    public void Test2()
    {
        byte[] code = Prepare.EvmCode
            .PushData(1)
            .Op(Instruction.POP)
            .Done;

        TestAllTracerWithOutput result = Execute(code);
    }
}
