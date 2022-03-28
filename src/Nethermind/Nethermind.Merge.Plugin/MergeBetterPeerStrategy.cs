﻿//  Copyright (c) 2021 Demerzel Solutions Limited
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

using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Synchronization;
using Nethermind.Synchronization.ParallelSync;

namespace Nethermind.Merge.Plugin;

public class MergeBetterPeerStrategy : IBetterPeerStrategy
{
    private readonly IBetterPeerStrategy _preMergeBetterPeerStrategy;
    private readonly ISyncProgressResolver _syncProgressResolver;
    private readonly IPoSSwitcher _poSSwitcher;

    public MergeBetterPeerStrategy(
        IBetterPeerStrategy preMergeBetterPeerStrategy,
        ISyncProgressResolver syncProgressResolver,
        IPoSSwitcher poSSwitcher)
    {
        _preMergeBetterPeerStrategy = preMergeBetterPeerStrategy;
        _syncProgressResolver = syncProgressResolver;
        _poSSwitcher = poSSwitcher;
    }
    
    public int Compare(BlockHeader? header, ISyncPeer? peerInfo)
    {
        UInt256 headerDifficulty = header?.TotalDifficulty ?? 0;
        UInt256 peerDifficulty = peerInfo?.TotalDifficulty ?? 0;
        if (ShouldApplyPreMergeLogic(headerDifficulty, peerDifficulty))
            return _preMergeBetterPeerStrategy.Compare(header, peerInfo);

        return (header?.Number ?? 0).CompareTo((peerInfo?.HeadNumber ?? 0));
    }

    public int Compare((UInt256 TotalDifficulty, long Number) value, ISyncPeer? peerInfo)
    {
        UInt256 totalDifficulty = value.TotalDifficulty;
        UInt256 peerDifficulty = peerInfo?.TotalDifficulty ?? 0;
        if (ShouldApplyPreMergeLogic(totalDifficulty, peerDifficulty))
            return _preMergeBetterPeerStrategy.Compare(value, peerInfo);

        return value.Number.CompareTo(peerInfo?.HeadNumber ?? 0);
    }

    public bool IsBetterThanLocalChain((UInt256 TotalDifficulty, long Number) bestPeerInfo)
    {
        UInt256 localChainDifficulty = _syncProgressResolver.ChainDifficulty;
        if (ShouldApplyPreMergeLogic(bestPeerInfo.TotalDifficulty, localChainDifficulty))
            return _preMergeBetterPeerStrategy.IsBetterThanLocalChain(bestPeerInfo); 
        
        return bestPeerInfo.Number > _syncProgressResolver.FindBestFullBlock();
    }

    public bool IsDesiredPeer((UInt256 TotalDifficulty, long Number) bestPeerInfo, long bestHeader)
    {
        UInt256 localChainDifficulty = _syncProgressResolver.ChainDifficulty;
        if (ShouldApplyPreMergeLogic(bestPeerInfo.TotalDifficulty, localChainDifficulty))
            return _preMergeBetterPeerStrategy.IsBetterThanLocalChain(bestPeerInfo); 
        
        return bestPeerInfo.Number > bestHeader;
    }
    
    private bool ShouldApplyPreMergeLogic(UInt256 totalDifficultyX, UInt256 totalDifficultyY)
    {
        return _poSSwitcher.TerminalTotalDifficulty == null || totalDifficultyX < _poSSwitcher.TerminalTotalDifficulty ||
               totalDifficultyY < _poSSwitcher.TerminalTotalDifficulty;
    }
}
