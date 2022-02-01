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

using System;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.Trie
{
    public class TrieStatsCollector : ITreeVisitor
    {
        private readonly IKeyValueStore _codeKeyValueStore;
        private int _lastAccountNodeCount = 0;

        private readonly ILogger _logger;

        public TrieStatsCollector(IKeyValueStore codeKeyValueStore, ILogManager logManager)
        {
            _codeKeyValueStore = codeKeyValueStore ?? throw new ArgumentNullException(nameof(codeKeyValueStore));
            _logger = logManager.GetClassLogger();
        }

        public TrieStats Stats { get; } = new();

        public bool ShouldVisit(Keccak nextNode)
        {
            return true;
        }

        public void VisitTree(Keccak rootHash, TrieVisitContext trieVisitContext) { }

        public void VisitMissingNode(Keccak nodeHash, TrieVisitContext trieVisitContext)
        {
            if (trieVisitContext.IsStorage)
            {
                Interlocked.Increment(ref Stats._missingStorage);
            }
            else
            {
                Interlocked.Increment(ref Stats._missingState);
            }

            IncrementLevel(trieVisitContext);
        }

        public void VisitBranch(TrieNode node, TrieVisitContext trieVisitContext)
        {
            if (trieVisitContext.IsStorage)
            {
                Interlocked.Add(ref Stats._storageSize, node.FullRlp?.Length ?? 0);
                Interlocked.Increment(ref Stats._storageBranchCount);
            }
            else
            {
                Interlocked.Add(ref Stats._stateSize, node.FullRlp?.Length ?? 0);
                Interlocked.Increment(ref Stats._stateBranchCount);
            }

            IncrementLevel(trieVisitContext);
        }

        public void VisitExtension(TrieNode node, TrieVisitContext trieVisitContext)
        {
            if (trieVisitContext.IsStorage)
            {
                Interlocked.Add(ref Stats._storageSize, node.FullRlp?.Length ?? 0);
                Interlocked.Increment(ref Stats._storageExtensionCount);
            }
            else
            {
                Interlocked.Add(ref Stats._stateSize, node.FullRlp?.Length ?? 0);
                Interlocked.Increment(ref Stats._stateExtensionCount);
            }

            IncrementLevel(trieVisitContext);
        }

        public void VisitLeaf(TrieNode node, TrieVisitContext trieVisitContext, byte[] value = null)
        {
            if (Stats.NodesCount - _lastAccountNodeCount > 1_000_000)
            {
                _lastAccountNodeCount = Stats.NodesCount;
                _logger.Warn($"Collected info from {Stats.NodesCount} nodes. Missing CODE {Stats.MissingCode} STATE {Stats.MissingState} STORAGE {Stats.MissingStorage}");
            }

            long size = node.FullRlp?.Length ?? 0;
            if (trieVisitContext.IsStorage)
            {
                Interlocked.Add(ref Stats._storageSize, size);
                Interlocked.Increment(ref Stats._storageLeafCount);

                Stats.StorageSizes.AddOrUpdate(trieVisitContext.ParentAccount, size, (key, oldValue) => oldValue + size);
            }
            else
            {
                Interlocked.Add(ref Stats._stateSize, size);
                Interlocked.Increment(ref Stats._accountCount);

                
                    trieVisitContext.ParentAccount = node.Keccak;
            }

            IncrementLevel(trieVisitContext);
        }

        public void VisitCode(Keccak codeHash, TrieVisitContext trieVisitContext)
        {
            byte[] code = _codeKeyValueStore[codeHash.Bytes];
            if (code != null)
            {
                Interlocked.Add(ref Stats._codeSize, code.Length);
                Interlocked.Increment(ref Stats._codeCount);
            }
            else
            {
                Interlocked.Increment(ref Stats._missingCode);
            }
        }

        private void IncrementLevel(TrieVisitContext trieVisitContext)
        {
            int[] levels = trieVisitContext.IsStorage ? Stats._storageLevels : Stats._stateLevels;
            int index = trieVisitContext.PathLevel % 64;
            index = index == 0 ? 64 : index;
            Interlocked.Increment(ref levels[index - 1]);
        }

        public void ExitLeaf(TrieNode node, TrieVisitContext context)
        {
            if (!context.IsStorage && context.ParentAccount is not null)
            {
                if(Stats.StorageSizes.TryGetValue(context.ParentAccount, out long size))
                {
                    int statsKey = (int)(size / (1024 * 1024));
                    Stats.StorageStats.AddOrUpdate(statsKey, 1, (key, oldValue) => oldValue + 1);

                    Stats.StorageSizes.TryRemove(context.ParentAccount, out var _);
                }

                context.ParentAccount = null;
            }
        }
    }
}
