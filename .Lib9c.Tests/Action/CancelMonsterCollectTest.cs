namespace Lib9c.Tests.Action
{
    using System;
    using System.Collections.Generic;
    using Bencodex.Types;
    using Libplanet;
    using Libplanet.Action;
    using Libplanet.Assets;
    using Libplanet.Crypto;
    using Nekoyume;
    using Nekoyume.Action;
    using Nekoyume.Model.State;
    using Nekoyume.TableData;
    using Xunit;

    public class CancelMonsterCollectTest
    {
        private readonly Address _signer;
        private readonly TableSheets _tableSheets;
        private IAccountStateDelta _state;

        public CancelMonsterCollectTest()
        {
            _signer = default;
            _state = new State();
            Dictionary<string, string> sheets = TableSheetsImporter.ImportSheets();
            _tableSheets = new TableSheets(sheets);
            var agentState = new AgentState(_signer);
            var currency = new Currency("NCG", 2, minters: null);
            var goldCurrencyState = new GoldCurrencyState(currency);

            _state = _state
                .SetState(_signer, agentState.Serialize())
                .SetState(Addresses.GoldCurrency, goldCurrencyState.Serialize());

            foreach ((string key, string value) in sheets)
            {
                _state = _state
                    .SetState(Addresses.TableSheet.Derive(key), value.Serialize());
            }
        }

        [Theory]
        [InlineData(7, 1, 1)]
        [InlineData(6, 2, 40000)]
        [InlineData(5, 3, 120000)]
        [InlineData(4, 3, 160000)]
        public void Execute(int prevLevel, int collectionLevel, long blockIndex)
        {
            Address collectionAddress = MonsterCollectionState.DeriveAddress(_signer, 0);
            List<MonsterCollectionRewardSheet.RewardInfo> rewardInfos = _tableSheets.MonsterCollectionRewardSheet[prevLevel].Rewards;
            MonsterCollectionState monsterCollectionState = new MonsterCollectionState(collectionAddress, prevLevel, 0, _tableSheets.MonsterCollectionRewardSheet);
            Currency currency = _state.GetGoldCurrency();
            FungibleAssetValue balance = 0 * currency;
            foreach (var row in _tableSheets.MonsterCollectionSheet)
            {
                if (collectionLevel < row.Level && row.Level <= prevLevel)
                {
                    balance += row.RequiredGold * currency;
                }
            }

            Assert.All(monsterCollectionState.RewardLevelMap, kv => Assert.Equal(rewardInfos, kv.Value));

            _state = _state
                .SetState(collectionAddress, monsterCollectionState.Serialize())
                .MintAsset(collectionAddress, balance);

            CancelMonsterCollect action = new CancelMonsterCollect
            {
                collectRound = 0,
                level = collectionLevel,
            };

            IAccountStateDelta nextState = action.Execute(new ActionContext
            {
                PreviousStates = _state,
                Signer = _signer,
                BlockIndex = blockIndex,
            });

            MonsterCollectionState nextMonsterCollectionState = new MonsterCollectionState((Dictionary)nextState.GetState(collectionAddress));
            Assert.Equal(collectionLevel, nextMonsterCollectionState.Level);
            Assert.Equal(0 * currency, nextState.GetBalance(collectionAddress, currency));
            Assert.Equal(balance, nextState.GetBalance(_signer, currency));

            long rewardLevel = nextMonsterCollectionState.GetRewardLevel(blockIndex);
            List<MonsterCollectionRewardSheet.RewardInfo> nextRewardInfos = _tableSheets.MonsterCollectionRewardSheet[collectionLevel].Rewards;
            for (long i = rewardLevel; i < 4; i++)
            {
                Assert.Equal(nextRewardInfos, nextMonsterCollectionState.RewardLevelMap[i + 1]);
            }
        }

        [Fact]
        public void Execute_Throw_FailedLoadStateException_AgentState()
        {
            CancelMonsterCollect action = new CancelMonsterCollect
            {
                level = 0,
                collectRound = 0,
            };

            Assert.Throws<FailedLoadStateException>(() => action.Execute(new ActionContext
                {
                    PreviousStates = _state,
                    Signer = new PrivateKey().ToAddress(),
                    BlockIndex = 0,
                })
            );
        }

        [Fact]
        public void Execute_Throw_FailedLoadStateException_MonsterCollectionState()
        {
            CancelMonsterCollect action = new CancelMonsterCollect
            {
                level = 0,
                collectRound = 0,
            };

            Assert.Throws<FailedLoadStateException>(() => action.Execute(new ActionContext
                {
                    PreviousStates = _state,
                    Signer = _signer,
                    BlockIndex = 0,
                })
            );
        }

        [Theory]
        [InlineData(1, 1)]
        [InlineData(2, 6)]
        [InlineData(3, 0)]
        public void Execute_Throw_InvalidLevelException(int prevLevel, int level)
        {
            Address collectionAddress = MonsterCollectionState.DeriveAddress(_signer, 0);
            MonsterCollectionState monsterCollectionState = new MonsterCollectionState(collectionAddress, prevLevel, 0, _tableSheets.MonsterCollectionRewardSheet);

            _state = _state.SetState(collectionAddress, monsterCollectionState.Serialize());

            CancelMonsterCollect action = new CancelMonsterCollect
            {
                level = level,
                collectRound = 0,
            };

            Assert.Throws<InvalidLevelException>(() => action.Execute(new ActionContext
                {
                    PreviousStates = _state,
                    Signer = _signer,
                    BlockIndex = 0,
                })
            );
        }

        [Fact]
        public void Execute_Throw_MonsterCollectionExpiredException()
        {
            Address collectionAddress = MonsterCollectionState.DeriveAddress(_signer, 0);
            MonsterCollectionState monsterCollectionState = new MonsterCollectionState(collectionAddress, 2, 0, _tableSheets.MonsterCollectionRewardSheet);
            for (int i = 0; i < MonsterCollectionState.RewardCapacity; i++)
            {
                MonsterCollectionResult monsterCollectionResult = new MonsterCollectionResult(Guid.NewGuid(), default, new List<MonsterCollectionRewardSheet.RewardInfo>());
                monsterCollectionState.UpdateRewardMap(i + 1, monsterCollectionResult, 0);
            }

            Assert.True(monsterCollectionState.End);

            _state = _state.SetState(collectionAddress, monsterCollectionState.Serialize());

            CancelMonsterCollect action = new CancelMonsterCollect
            {
                level = 1,
                collectRound = 0,
            };

            Assert.Throws<MonsterCollectionExpiredException>(() => action.Execute(new ActionContext
                {
                    PreviousStates = _state,
                    Signer = _signer,
                    BlockIndex = 0,
                })
            );
        }

        [Fact]
        public void Execute_Throw_InsufficientBalanceException()
        {
            Address collectionAddress = MonsterCollectionState.DeriveAddress(_signer, 0);
            MonsterCollectionState monsterCollectionState = new MonsterCollectionState(collectionAddress, 2, 0, _tableSheets.MonsterCollectionRewardSheet);

            _state = _state.SetState(collectionAddress, monsterCollectionState.Serialize());

            CancelMonsterCollect action = new CancelMonsterCollect
            {
                level = 1,
                collectRound = 0,
            };

            Assert.Throws<InsufficientBalanceException>(() => action.Execute(new ActionContext
                {
                    PreviousStates = _state,
                    Signer = _signer,
                    BlockIndex = 0,
                })
            );
        }
    }
}
