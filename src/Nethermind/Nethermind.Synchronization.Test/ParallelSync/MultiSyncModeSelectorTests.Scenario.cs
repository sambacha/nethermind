using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.ParallelSync
{
    public partial class MultiSyncModeSelectorTests
    {
        public static class Scenario
        {
            public const long FastSyncCatchUpHeightDelta = 64;

            public static BlockHeader Pivot { get; set; } = Build.A.Block.WithDifficulty(1).WithTotalDifficulty((UInt256)1024).WithNumber(1024).TestObject.Header;

            public static BlockHeader MidWayToPivot { get; set; } = Build.A.Block.WithDifficulty(1).WithTotalDifficulty((UInt256)512).WithNumber(512).TestObject.Header;

            public static BlockHeader ChainHead { get; set; } = Build.A.Block.WithDifficulty(1).WithTotalDifficulty(Pivot.TotalDifficulty + 2048).WithNumber(Pivot.Number + 2048).TestObject.Header;

            public static BlockHeader ChainHeadWrongDifficulty
            {
                get
                {
                    BlockHeader header = Build.A.Block.WithDifficulty(1).WithTotalDifficulty(Pivot.TotalDifficulty + 2048 + 128).WithNumber(Pivot.Number + 2048).TestObject.Header;
                    header.Hash = ChainHead.Hash;
                    return header;
                }
            }

            public static BlockHeader ChainHeadParentWrongDifficulty
            {
                get
                {
                    BlockHeader header = Build.A.Block.WithDifficulty(1).WithTotalDifficulty(Pivot.TotalDifficulty + 2048 + 128).WithNumber(Pivot.Number + 2048).TestObject.Header;
                    header.Hash = ChainHead.ParentHash;
                    return header;
                }
            }

            public static BlockHeader FutureHead { get; set; } = Build.A.Block.WithDifficulty(1).WithTotalDifficulty(Pivot.TotalDifficulty + 2048 + 128).WithNumber(Pivot.Number + 2048 + 128).TestObject.Header;

            public static BlockHeader SlightlyFutureHead { get; set; } = Build.A.Block.WithDifficulty(1).WithTotalDifficulty(Pivot.TotalDifficulty + 2048 + 4).WithNumber(Pivot.Number + 2048 + 4).TestObject.Header;

            public static BlockHeader SlightlyFutureHeadWithFastSyncLag { get; set; } = Build.A.Block.WithDifficulty(1).WithTotalDifficulty(Pivot.TotalDifficulty + 2048 + 4).WithNumber(ChainHead.Number + MultiSyncModeSelector.FastSyncLag + 1).TestObject.Header;

            public static BlockHeader MaliciousPrePivot { get; set; } = Build.A.Block.WithDifficulty(1).WithTotalDifficulty((UInt256)1000000).WithNumber(512).TestObject.Header;

            public static BlockHeader NewBetterBranchWithLowerNumber { get; set; } = Build.A.Block.WithDifficulty(1).WithTotalDifficulty((UInt256)1000000).WithNumber(ChainHead.Number - 16).TestObject.Header;

            public static BlockHeader ValidGenesis { get; set; } = Build.A.Block.WithDifficulty(1).WithTotalDifficulty(UInt256.One).Genesis.TestObject.Header;

            public static BlockHeader InvalidGenesis { get; set; } = Build.A.Block.WithDifficulty(1).WithTotalDifficulty(UInt256.One).Genesis.TestObject.Header;

            public static BlockHeader InvalidGenesisWithHighTotalDifficulty { get; set; } = Build.A.Block.Genesis.WithDifficulty((UInt256)1000000).WithTotalDifficulty((UInt256)1000000).TestObject.Header;

            public static IEnumerable<BlockHeader> ScenarioHeaders
            {
                get
                {
                    yield return Pivot;
                    yield return MidWayToPivot;
                    yield return ChainHead;
                    yield return ChainHeadWrongDifficulty;
                    yield return ChainHeadParentWrongDifficulty;
                    yield return FutureHead;
                    yield return SlightlyFutureHead;
                    yield return MaliciousPrePivot;
                    yield return NewBetterBranchWithLowerNumber;
                    yield return ValidGenesis;
                    yield return InvalidGenesis;
                    yield return InvalidGenesisWithHighTotalDifficulty;
                }
            }

            public class ScenarioBuilder
            {
                private readonly List<Func<string>> _configActions = new();

                private readonly List<Func<string>> _peeringSetups = new();

                private readonly List<Func<string>> _syncProgressSetups = new();

                private readonly List<Action> _overwrites = new();

                private readonly List<ISyncPeer> _peers = new();
                private bool _needToWaitForHeaders;

                public ISyncPeerPool SyncPeerPool { get; set; }

                public ISyncProgressResolver SyncProgressResolver { get; set; }

                public ISyncConfig SyncConfig { get; set; } = new SyncConfig();

                public ScenarioBuilder()
                {
                }

                private void SetDefaults()
                {
                    SyncPeerPool = Substitute.For<ISyncPeerPool>();
                    var peerInfos = _peers.Select(p => new PeerInfo(p));
                    SyncPeerPool.InitializedPeers.Returns(peerInfos);
                    SyncPeerPool.AllPeers.Returns(peerInfos);

                    SyncProgressResolver = Substitute.For<ISyncProgressResolver>();
                    SyncProgressResolver.ChainDifficulty.Returns(ValidGenesis.TotalDifficulty ?? 0);
                    SyncProgressResolver.FindBestHeader().Returns(0);
                    SyncProgressResolver.FindBestFullBlock().Returns(0);
                    SyncProgressResolver.FindBestFullState().Returns(0);
                    SyncProgressResolver.IsLoadingBlocksFromDb().Returns(false);
                    SyncProgressResolver.IsFastBlocksFinished().Returns(FastBlocksState.None);

                    SyncConfig.FastSync = false;
                    SyncConfig.FastBlocks = false;
                    SyncConfig.PivotNumber = Pivot.Number.ToString();
                    SyncConfig.PivotHash = Keccak.Zero.ToString();
                    SyncConfig.SynchronizationEnabled = true;
                    SyncConfig.NetworkingEnabled = true;
                    SyncConfig.DownloadBodiesInFastSync = true;
                    SyncConfig.DownloadReceiptsInFastSync = true;
                    SyncConfig.FastSyncCatchUpHeightDelta = FastSyncCatchUpHeightDelta;
                }

                private void AddPeeringSetup(string name, params ISyncPeer[] peers)
                {
                    _peeringSetups.Add(() =>
                    {
                        foreach (ISyncPeer syncPeer in peers)
                        {
                            _peers.Add(syncPeer);
                        }

                        return name;
                    });
                }

                private ISyncPeer AddPeer(BlockHeader header, bool isInitialized = true, string clientType = "Nethermind")
                {
                    ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
                    syncPeer.HeadHash.Returns(header.Hash);
                    syncPeer.HeadNumber.Returns(header.Number);
                    syncPeer.TotalDifficulty.Returns(header.TotalDifficulty ?? 0);
                    syncPeer.IsInitialized.Returns(isInitialized);
                    syncPeer.ClientId.Returns(clientType);
                    return syncPeer;
                }

                public ScenarioBuilder IfThisNodeHasNeverSyncedBefore()
                {
                    _syncProgressSetups.Add(() => "fresh start");
                    return this;
                }

                public ScenarioBuilder IfThisNodeIsFullySynced()
                {
                    _syncProgressSetups.Add(
                        () =>
                        {
                            SyncProgressResolver.FindBestHeader().Returns(ChainHead.Number);
                            SyncProgressResolver.FindBestFullBlock().Returns(ChainHead.Number);
                            SyncProgressResolver.FindBestFullState().Returns(ChainHead.Number);
                            SyncProgressResolver.FindBestProcessedBlock().Returns(ChainHead.Number);
                            SyncProgressResolver.GetTotalDifficulty(Arg.Any<Keccak>()).Returns(info =>
                            {
                                var hash = info.Arg<Keccak>();

                                foreach (BlockHeader scenarioHeader in ScenarioHeaders)
                                {
                                    if (scenarioHeader.Hash == hash)
                                    {
                                        return scenarioHeader.TotalDifficulty;
                                    }
                                    else if (scenarioHeader.ParentHash == hash)
                                    {
                                        return scenarioHeader.TotalDifficulty - scenarioHeader.Difficulty;
                                    }
                                }

                                return null;
                            });
                            SyncProgressResolver.IsFastBlocksFinished().Returns(FastBlocksState.FinishedReceipts);
                            SyncProgressResolver.ChainDifficulty.Returns(ChainHead.TotalDifficulty ?? 0);
                            return "fully synced node";
                        }
                    );
                    return this;
                }

                public ScenarioBuilder IfThisNodeIsProcessingAlreadyDownloadedBlocksInFullSync()
                {
                    _syncProgressSetups.Add(
                        () =>
                        {
                            SyncProgressResolver.FindBestHeader().Returns(ChainHead.Number);
                            SyncProgressResolver.FindBestFullBlock().Returns(ChainHead.Number);
                            SyncProgressResolver.FindBestFullState().Returns(ChainHead.Number - FastSyncCatchUpHeightDelta + 1);
                            SyncProgressResolver.FindBestProcessedBlock().Returns(ChainHead.Number - FastSyncCatchUpHeightDelta + 1);
                            SyncProgressResolver.IsFastBlocksFinished().Returns(FastBlocksState.FinishedReceipts);
                            SyncProgressResolver.ChainDifficulty.Returns(ChainHead.TotalDifficulty ?? 0);
                            return "fully syncing";
                        }
                    );
                    return this;
                }

                public ScenarioBuilder IfPeersMovedForwardBeforeThisNodeProcessedFirstFullBlock()
                {
                    _syncProgressSetups.Add(
                        () =>
                        {
                            SyncProgressResolver.FindBestHeader().Returns(ChainHead.Number);
                            SyncProgressResolver.FindBestFullBlock().Returns(ChainHead.Number);
                            SyncProgressResolver.FindBestFullState().Returns(ChainHead.Number - 2);
                            SyncProgressResolver.FindBestProcessedBlock().Returns(0);
                            SyncProgressResolver.IsFastBlocksFinished().Returns(FastBlocksState.None);
                            SyncProgressResolver.ChainDifficulty.Returns((ChainHead.TotalDifficulty ?? 0) + (UInt256)2);
                            return "fully syncing";
                        }
                    );
                    return this;
                }

                public ScenarioBuilder IfThisNodeIsInTheMiddleOfFastSyncAndFastBlocks()
                {
                    _syncProgressSetups.Add(
                        () =>
                        {
                            SyncProgressResolver.FindBestHeader().Returns(Pivot.Number + 16);
                            SyncProgressResolver.FindBestFullBlock().Returns(0);
                            SyncProgressResolver.FindBestFullState().Returns(0);
                            SyncProgressResolver.FindBestProcessedBlock().Returns(0);
                            SyncProgressResolver.IsFastBlocksFinished().Returns(FastBlocksState.None);
                            SyncProgressResolver.ChainDifficulty.Returns(UInt256.Zero);
                            return "mid fast sync and fast blocks";
                        }
                    );
                    return this;
                }

                public ScenarioBuilder IfThisNodeFinishedFastBlocksButNotFastSync()
                {
                    _syncProgressSetups.Add(
                        () =>
                        {
                            SyncProgressResolver.FindBestHeader().Returns(Pivot.Number + 16);
                            SyncProgressResolver.FindBestFullBlock().Returns(0);
                            SyncProgressResolver.FindBestFullState().Returns(0);
                            SyncProgressResolver.FindBestProcessedBlock().Returns(0);
                            SyncProgressResolver.IsFastBlocksFinished().Returns(FastBlocksState.FinishedReceipts);
                            SyncProgressResolver.ChainDifficulty.Returns(UInt256.Zero);
                            return "mid fast sync";
                        }
                    );
                    return this;
                }

                public ScenarioBuilder IfThisNodeJustCameBackFromBeingOfflineForLongTimeAndFinishedFastSyncCatchUp()
                {
                    _syncProgressSetups.Add(
                        () =>
                        {
                            SyncProgressResolver.FindBestHeader().Returns(ChainHead.Number);
                            SyncProgressResolver.FindBestFullBlock().Returns(ChainHead.Number);
                            SyncProgressResolver.FindBestFullState().Returns(ChainHead.Number - FastSyncCatchUpHeightDelta - 1);
                            SyncProgressResolver.FindBestProcessedBlock().Returns(ChainHead.Number - FastSyncCatchUpHeightDelta - 1);
                            SyncProgressResolver.IsFastBlocksFinished().Returns(FastBlocksState.FinishedReceipts);
                            SyncProgressResolver.ChainDifficulty.Returns(UInt256.Zero);
                            return "mid fast sync";
                        }
                    );
                    return this;
                }

                public ScenarioBuilder ThisNodeFinishedFastSyncButNotFastBlocks()
                {
                    _syncProgressSetups.Add(
                        () =>
                        {
                            SyncProgressResolver.FindBestHeader().Returns(ChainHead.Number - MultiSyncModeSelector.FastSyncLag);
                            SyncProgressResolver.FindBestFullBlock().Returns(0);
                            SyncProgressResolver.FindBestFullState().Returns(0);
                            SyncProgressResolver.FindBestProcessedBlock().Returns(0);
                            SyncProgressResolver.IsFastBlocksFinished().Returns(FastBlocksState.None);
                            SyncProgressResolver.ChainDifficulty.Returns(UInt256.Zero);
                            return "mid fast blocks but fast sync finished";
                        }
                    );
                    return this;
                }

                public ScenarioBuilder IfThisNodeFinishedStateSyncButNotFastBlocks(FastBlocksState fastBlocksState = FastBlocksState.None)
                {
                    _syncProgressSetups.Add(
                        () =>
                        {
                            SyncProgressResolver.FindBestHeader().Returns(ChainHead.Number - MultiSyncModeSelector.FastSyncLag);
                            SyncProgressResolver.FindBestFullBlock().Returns(0);
                            SyncProgressResolver.FindBestFullState().Returns(ChainHead.Number - MultiSyncModeSelector.FastSyncLag);
                            SyncProgressResolver.FindBestProcessedBlock().Returns(0);
                            SyncProgressResolver.IsFastBlocksFinished().Returns(fastBlocksState);
                            SyncProgressResolver.ChainDifficulty.Returns(UInt256.Zero);
                            return "just finished state sync but not fast blocks";
                        }
                    );
                    return this;
                }

                public ScenarioBuilder IfThisNodeJustFinishedStateSyncAndFastBlocks(FastBlocksState fastBlocksState = FastBlocksState.FinishedReceipts)
                {
                    _syncProgressSetups.Add(
                        () =>
                        {
                            SyncProgressResolver.FindBestHeader().Returns(ChainHead.Number - MultiSyncModeSelector.FastSyncLag);
                            SyncProgressResolver.FindBestFullBlock().Returns(0);
                            SyncProgressResolver.FindBestFullState().Returns(ChainHead.Number - MultiSyncModeSelector.FastSyncLag);
                            SyncProgressResolver.FindBestProcessedBlock().Returns(0);
                            SyncProgressResolver.IsFastBlocksFinished().Returns(fastBlocksState);
                            SyncProgressResolver.ChainDifficulty.Returns(UInt256.Zero);
                            return "just finished state sync and fast blocks";
                        }
                    );
                    return this;
                }

                public ScenarioBuilder IfThisNodeJustFinishedStateSyncButNeedsToCatchUpToHeaders()
                {
                    _syncProgressSetups.Add(
                        () =>
                        {
                            SyncProgressResolver.FindBestHeader().Returns(ChainHead.Number - MultiSyncModeSelector.FastSyncLag);
                            SyncProgressResolver.FindBestFullBlock().Returns(0);
                            SyncProgressResolver.FindBestFullState().Returns(ChainHead.Number - MultiSyncModeSelector.FastSyncLag - 7);
                            SyncProgressResolver.FindBestProcessedBlock().Returns(0);
                            SyncProgressResolver.IsFastBlocksFinished().Returns(FastBlocksState.FinishedReceipts);
                            SyncProgressResolver.ChainDifficulty.Returns(UInt256.Zero);
                            return "just finished state sync and needs to catch up";
                        }
                    );
                    return this;
                }

                public ScenarioBuilder IfThisNodeJustFinishedStateSyncCatchUp()
                {
                    _syncProgressSetups.Add(
                        () =>
                        {
                            SyncProgressResolver.FindBestHeader().Returns(ChainHead.Number);
                            SyncProgressResolver.FindBestFullBlock().Returns(ChainHead.Number);
                            SyncProgressResolver.FindBestFullState().Returns(ChainHead.Number - MultiSyncModeSelector.FastSyncLag);
                            SyncProgressResolver.FindBestProcessedBlock().Returns(0);
                            SyncProgressResolver.IsFastBlocksFinished().Returns(FastBlocksState.FinishedReceipts);
                            SyncProgressResolver.ChainDifficulty.Returns(UInt256.Zero);
                            return "just finished state sync catch up";
                        }
                    );
                    return this;
                }

                public ScenarioBuilder IfThisNodeJustFinishedFastBlocksAndFastSync(FastBlocksState fastBlocksState = FastBlocksState.FinishedReceipts)
                {
                    _syncProgressSetups.Add(
                        () =>
                        {
                            SyncProgressResolver.FindBestHeader().Returns(ChainHead.Number - MultiSyncModeSelector.FastSyncLag);
                            SyncProgressResolver.FindBestFullBlock().Returns(0);
                            SyncProgressResolver.FindBestFullState().Returns(0);
                            SyncProgressResolver.FindBestProcessedBlock().Returns(0);
                            SyncProgressResolver.IsFastBlocksFinished().Returns(fastBlocksState);
                            SyncProgressResolver.ChainDifficulty.Returns(UInt256.Zero);
                            return "just after fast blocks and fast sync";
                        }
                    );
                    return this;
                }

                public ScenarioBuilder IfThisNodeJustStartedFullSyncProcessing(FastBlocksState fastBlocksState = FastBlocksState.FinishedReceipts)
                {
                    long currentBlock = ChainHead.Number - MultiSyncModeSelector.FastSyncLag + 1;
                    _syncProgressSetups.Add(
                        () =>
                        {
                            SyncProgressResolver.FindBestHeader().Returns(currentBlock);
                            SyncProgressResolver.FindBestFullBlock().Returns(currentBlock);
                            SyncProgressResolver.FindBestFullState().Returns(currentBlock);
                            SyncProgressResolver.FindBestProcessedBlock().Returns(0);
                            SyncProgressResolver.IsFastBlocksFinished().Returns(fastBlocksState);
                            SyncProgressResolver.ChainDifficulty.Returns((UInt256)currentBlock);
                            return "just started full sync";
                        }
                    );
                    return this;
                }

                public ScenarioBuilder IfThisNodeRecentlyStartedFullSyncProcessing(FastBlocksState fastBlocksState = FastBlocksState.FinishedReceipts)
                {
                    long currentBlock = ChainHead.Number - MultiSyncModeSelector.FastSyncLag / 2;
                    _syncProgressSetups.Add(
                        () =>
                        {
                            SyncProgressResolver.FindBestHeader().Returns(ChainHead.Number);
                            SyncProgressResolver.FindBestFullBlock().Returns(ChainHead.Number);
                            SyncProgressResolver.FindBestFullState().Returns(currentBlock);
                            SyncProgressResolver.FindBestProcessedBlock().Returns(currentBlock);
                            SyncProgressResolver.IsFastBlocksFinished().Returns(fastBlocksState);
                            SyncProgressResolver.ChainDifficulty.Returns((UInt256)currentBlock);
                            return "recently started full sync";
                        }
                    );
                    return this;
                }

                /// <summary>
                /// Empty clique chains do not update state root on empty blocks (no block reward)
                /// </summary>
                /// <returns></returns>
                public ScenarioBuilder IfThisNodeRecentlyStartedFullSyncProcessingOnEmptyCliqueChain()
                {
                    // so the state root check can think that state root is after processed
                    _syncProgressSetups.Add(
                        () =>
                        {
                            SyncProgressResolver.FindBestHeader().Returns(ChainHead.Number);
                            SyncProgressResolver.FindBestFullBlock().Returns(ChainHead.Number);
                            SyncProgressResolver.FindBestFullState().Returns(ChainHead.Number - MultiSyncModeSelector.FastSyncLag + 1);
                            SyncProgressResolver.FindBestProcessedBlock().Returns(ChainHead.Number - MultiSyncModeSelector.FastSyncLag);
                            SyncProgressResolver.IsFastBlocksFinished().Returns(FastBlocksState.FinishedReceipts);
                            SyncProgressResolver.ChainDifficulty.Returns((UInt256)ChainHead.Number - MultiSyncModeSelector.FastSyncLag);
                            return "recently started full sync on empty clique chain";
                        }
                    );
                    return this;
                }

                public ScenarioBuilder IfThisNodeNeedsAFastSyncCatchUp()
                {
                    long currentBlock = ChainHead.Number - FastSyncCatchUpHeightDelta;
                    _syncProgressSetups.Add(
                        () =>
                        {
                            SyncProgressResolver.FindBestHeader().Returns(currentBlock);
                            SyncProgressResolver.FindBestFullBlock().Returns(currentBlock);
                            SyncProgressResolver.FindBestFullState().Returns(currentBlock);
                            SyncProgressResolver.FindBestProcessedBlock().Returns(currentBlock);
                            SyncProgressResolver.IsFastBlocksFinished().Returns(FastBlocksState.FinishedReceipts);
                            SyncProgressResolver.ChainDifficulty.Returns((UInt256)currentBlock);
                            return "fast sync catch up";
                        }
                    );
                    return this;
                }

                public ScenarioBuilder IfThisNodeHasStateThatIsFarInThePast()
                {
                    // this is a scenario when we actually have state but the lookup depth is limiting
                    // our ability to find out at what level the state is
                    long currentBlock = ChainHead.Number - FastSyncCatchUpHeightDelta - 16;
                    _syncProgressSetups.Add(
                        () =>
                        {
                            SyncProgressResolver.FindBestHeader().Returns(ChainHead.Number);
                            SyncProgressResolver.FindBestFullBlock().Returns(ChainHead.Number);
                            SyncProgressResolver.FindBestFullState().Returns(0);
                            SyncProgressResolver.FindBestProcessedBlock().Returns(currentBlock);
                            SyncProgressResolver.IsFastBlocksFinished().Returns(FastBlocksState.FinishedReceipts);
                            SyncProgressResolver.ChainDifficulty.Returns((UInt256)currentBlock);
                            return "fast sync catch up";
                        }
                    );
                    return this;
                }

                public ScenarioBuilder IfThisNodeNearlyNeedsAFastSyncCatchUp()
                {
                    long currentBlock = ChainHead.Number - FastSyncCatchUpHeightDelta + 1;
                    _syncProgressSetups.Add(
                        () =>
                        {
                            SyncProgressResolver.FindBestHeader().Returns(currentBlock);
                            SyncProgressResolver.FindBestFullBlock().Returns(currentBlock);
                            SyncProgressResolver.FindBestFullState().Returns(currentBlock);
                            SyncProgressResolver.FindBestProcessedBlock().Returns(currentBlock);
                            SyncProgressResolver.IsFastBlocksFinished().Returns(FastBlocksState.FinishedReceipts);
                            SyncProgressResolver.ChainDifficulty.Returns((UInt256)currentBlock);
                            return "fast sync catch up";
                        }
                    );
                    return this;
                }

                public ScenarioBuilder IfTheSyncProgressIsCorrupted()
                {
                    _syncProgressSetups.Add(
                        () =>
                        {
                            SyncProgressResolver.FindBestHeader().Returns(ChainHead.Number);
                            SyncProgressResolver.FindBestFullBlock().Returns(ChainHead.Number);

                            SyncProgressResolver.FindBestFullState().Returns(ChainHead.Number - 1);
                            SyncProgressResolver.FindBestProcessedBlock().Returns(ChainHead.Number);
                            SyncProgressResolver.IsFastBlocksFinished().Returns(FastBlocksState.FinishedReceipts);
                            SyncProgressResolver.ChainDifficulty.Returns((UInt256)ChainHead.Number);
                            return "corrupted progress";
                        }
                    );

                    return this;
                }

                public ScenarioBuilder AndAPeerWithGenesisOnlyIsKnown()
                {
                    AddPeeringSetup("genesis network", AddPeer(ValidGenesis));
                    return this;
                }

                public ScenarioBuilder AndAPeerWithHighDiffGenesisOnlyIsKnown()
                {
                    AddPeeringSetup("malicious genesis network", AddPeer(ValidGenesis));
                    return this;
                }

                public ScenarioBuilder AndGoodPeersAreKnown()
                {
                    AddPeeringSetup("good network", AddPeer(ChainHead));
                    return this;
                }

                public ScenarioBuilder AndPeersMovedForward()
                {
                    AddPeeringSetup("peers moved forward", AddPeer(FutureHead));
                    return this;
                }

                public ScenarioBuilder AndPeersMovedSlightlyForward()
                {
                    AddPeeringSetup("peers moved slightly forward", AddPeer(SlightlyFutureHead));
                    return this;
                }

                public ScenarioBuilder AndPeersMovedSlightlyForwardWithFastSyncLag()
                {
                    AddPeeringSetup("peers moved slightly forward", AddPeer(SlightlyFutureHeadWithFastSyncLag));
                    return this;
                }

                public ScenarioBuilder PeersFromDesirableBranchAreKnown()
                {
                    AddPeeringSetup("better branch", AddPeer(NewBetterBranchWithLowerNumber));
                    return this;
                }

                public ScenarioBuilder PeersWithWrongDifficultyAreKnown()
                {
                    AddPeeringSetup("wrong difficulty", AddPeer(ChainHeadWrongDifficulty), AddPeer(ChainHeadParentWrongDifficulty));
                    return this;
                }

                public ScenarioBuilder AndDesirablePrePivotPeerIsKnown()
                {
                    AddPeeringSetup("good network", AddPeer(MaliciousPrePivot));
                    return this;
                }

                public ScenarioBuilder AndPeersAreOnlyUsefulForFastBlocks()
                {
                    AddPeeringSetup("network for fast blocks only", AddPeer(MidWayToPivot));
                    return this;
                }

                public ScenarioBuilder AndNoPeersAreKnown()
                {
                    AddPeeringSetup("empty network");
                    return this;
                }

                public ScenarioBuilder WhenSynchronizationIsDisabled()
                {
                    _overwrites.Add(() => SyncConfig.SynchronizationEnabled = false);
                    return this;
                }

                public ScenarioBuilder WhenThisNodeIsLoadingBlocksFromDb()
                {
                    _overwrites.Add(() => SyncProgressResolver.IsLoadingBlocksFromDb().Returns(true));
                    return this;
                }

                public ScenarioBuilder ThenInAnySyncConfiguration()
                {
                    WhenFullArchiveSyncIsConfigured();
                    WhenFastSyncWithFastBlocksIsConfigured();
                    WhenFastSyncWithoutFastBlocksIsConfigured();
                    return this;
                }

                public ScenarioBuilder ThenInAnyFastSyncConfiguration()
                {
                    WhenFastSyncWithFastBlocksIsConfigured();
                    WhenFastSyncWithoutFastBlocksIsConfigured();
                    return this;
                }

                public ScenarioBuilder WhateverThePeerPoolLooks()
                {
                    AndNoPeersAreKnown();
                    AndGoodPeersAreKnown();
                    AndPeersMovedForward();
                    AndPeersMovedSlightlyForward();
                    AndDesirablePrePivotPeerIsKnown();
                    AndAPeerWithHighDiffGenesisOnlyIsKnown();
                    AndAPeerWithGenesisOnlyIsKnown();
                    AndPeersAreOnlyUsefulForFastBlocks();
                    PeersFromDesirableBranchAreKnown();
                    return this;
                }

                public ScenarioBuilder WhateverTheSyncProgressIs()
                {
                    var fastBlocksStates = Enum.GetValues(typeof(FastBlocksState)).Cast<FastBlocksState>().ToList();
                    IfThisNodeJustCameBackFromBeingOfflineForLongTimeAndFinishedFastSyncCatchUp();
                    IfThisNodeHasNeverSyncedBefore();
                    IfThisNodeIsFullySynced();
                    IfThisNodeIsProcessingAlreadyDownloadedBlocksInFullSync();
                    IfThisNodeIsInTheMiddleOfFastSyncAndFastBlocks();
                    IfThisNodeFinishedFastBlocksButNotFastSync();
                    fastBlocksStates.ForEach(s => IfThisNodeJustFinishedFastBlocksAndFastSync(s));
                    IfThisNodeFinishedStateSyncButNotFastBlocks();
                    IfThisNodeJustFinishedStateSyncButNeedsToCatchUpToHeaders();
                    fastBlocksStates.ForEach(s => IfThisNodeJustFinishedStateSyncAndFastBlocks(s));
                    fastBlocksStates.ForEach(s => IfThisNodeJustStartedFullSyncProcessing(s));
                    fastBlocksStates.ForEach(s => IfThisNodeRecentlyStartedFullSyncProcessing(s));
                    IfTheSyncProgressIsCorrupted();
                    IfThisNodeNeedsAFastSyncCatchUp();
                    IfThisNodeJustFinishedStateSyncCatchUp();
                    IfThisNodeNearlyNeedsAFastSyncCatchUp();
                    IfThisNodeHasStateThatIsFarInThePast();
                    IfThisNodeRecentlyStartedFullSyncProcessingOnEmptyCliqueChain();
                    return this;
                }

                public ScenarioBuilder WhenFastSyncWithFastBlocksIsConfigured()
                {
                    _configActions.Add(() =>
                    {
                        SyncConfig.FastSync = true;
                        SyncConfig.FastBlocks = true;
                        return "fast sync with fast blocks";
                    });

                    return this;
                }

                public ScenarioBuilder WhenFastSyncWithoutFastBlocksIsConfigured()
                {
                    _configActions.Add(() =>
                    {
                        SyncConfig.FastSync = true;
                        SyncConfig.FastBlocks = false;
                        return "fast sync without fast blocks";
                    });

                    return this;
                }

                public ScenarioBuilder WhenFullArchiveSyncIsConfigured()
                {
                    _configActions.Add(() =>
                    {
                        SyncConfig.FastSync = false;
                        SyncConfig.FastBlocks = false;
                        return "full archive";
                    });

                    return this;
                }

                public void TheSyncModeShouldBe(SyncMode syncMode)
                {
                    void Test()
                    {
                        foreach (Action overwrite in _overwrites)
                        {
                            overwrite.Invoke();
                        }

                        MultiSyncModeSelector selector = new(SyncProgressResolver, SyncPeerPool, SyncConfig, LimboLogs.Instance, _needToWaitForHeaders);
                        selector.DisableTimer();
                        selector.Update();
                        selector.Current.Should().Be(syncMode);
                    }

                    SetDefaults();

                    if (_syncProgressSetups.Count == 0 || _peeringSetups.Count == 0 || _configActions.Count == 0)
                        throw new ArgumentException($"Invalid test configuration. _syncProgressSetups.Count {_syncProgressSetups.Count}, _peeringSetups.Count {_peeringSetups.Count}, _configActions.Count {_configActions.Count}");
                    foreach (Func<string> syncProgressSetup in _syncProgressSetups)
                    {
                        foreach (Func<string> peeringSetup in _peeringSetups)
                        {
                            foreach (Func<string> configSetups in _configActions)
                            {
                                string syncProgressSetupName = syncProgressSetup.Invoke();
                                string peeringSetupName = peeringSetup.Invoke();
                                string configSetupName = configSetups.Invoke();

                                Console.WriteLine("=====================");
                                Console.WriteLine(syncProgressSetupName);
                                Console.WriteLine(peeringSetupName);
                                Console.WriteLine(configSetupName);
                                Test();
                                Console.WriteLine("=====================");
                            }
                        }
                    }
                }

                public ScenarioBuilder WhenConsensusRequiresToWaitForHeaders(bool needToWaitForHeaders)
                {
                    _needToWaitForHeaders = needToWaitForHeaders;
                    return this;
                }
            }

            public static ScenarioBuilder GoesLikeThis(bool needToWaitForHeaders) =>
                new ScenarioBuilder().WhenConsensusRequiresToWaitForHeaders(needToWaitForHeaders);
        }
    }
}
