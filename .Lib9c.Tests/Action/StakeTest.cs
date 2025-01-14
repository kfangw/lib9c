namespace Lib9c.Tests.Action
{
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using Bencodex.Types;
    using Libplanet;
    using Libplanet.Action;
    using Libplanet.Assets;
    using Nekoyume;
    using Nekoyume.Action;
    using Nekoyume.Model.State;
    using Nekoyume.TableData;
    using Xunit;

    public class StakeTest
    {
        private readonly TableSheets _tableSheets;
        private readonly Address _signer;
        private IAccountStateDelta _initialState;

        public StakeTest()
        {
            Dictionary<string, string> sheets = TableSheetsImporter.ImportSheets();
            _tableSheets = new TableSheets(sheets);
            _signer = default;
            var currency = new Currency("NCG", 2, minters: null);
            var goldCurrencyState = new GoldCurrencyState(currency);
            _initialState = new State()
                .SetState(Addresses.GoldCurrency, goldCurrencyState.Serialize());
            foreach ((string key, string value) in sheets)
            {
                _initialState = _initialState
                    .SetState(Addresses.TableSheet.Derive(key), value.Serialize())
                    .SetState(_signer, new AgentState(_signer).Serialize());
            }
        }

        [Theory]
        [InlineData(true, 2, 1, 1, 1)]
        [InlineData(true, 5, 2, 2, 40000)]
        [InlineData(false, 1, 3, 0, 120000)]
        [InlineData(false, 3, 4, 0, 160000)]
        public void Execute(bool exist, int level, int monsterCollectionRound, int prevLevel, long blockIndex)
        {
            Address monsterCollectionAddress = MonsterCollectionState.DeriveAddress(_signer, monsterCollectionRound);
            if (exist)
            {
                List<MonsterCollectionRewardSheet.RewardInfo> rewards = _tableSheets.MonsterCollectionRewardSheet[prevLevel].Rewards;
                MonsterCollectionState prevMonsterCollectionState = new MonsterCollectionState(monsterCollectionAddress, prevLevel, 0, _tableSheets.MonsterCollectionRewardSheet);
                _initialState = _initialState.SetState(monsterCollectionAddress, prevMonsterCollectionState.Serialize());
                Assert.All(prevMonsterCollectionState.RewardLevelMap, kv => Assert.Equal(rewards, kv.Value));
            }

            AgentState prevAgentState = _initialState.GetAgentState(_signer);
            while (prevAgentState.MonsterCollectionRound < monsterCollectionRound)
            {
                prevAgentState.IncreaseCollectionRound();
            }

            _initialState = _initialState.SetState(_signer, prevAgentState.Serialize());

            Currency currency = _initialState.GetGoldCurrency();

            for (int i = 1; i < level + 1; i++)
            {
                if (i > prevLevel)
                {
                    MonsterCollectionSheet.Row row = _tableSheets.MonsterCollectionSheet[i];
                    _initialState = _initialState.MintAsset(_signer, row.RequiredGold * currency);
                }
            }

            MonsterCollect action = new MonsterCollect
            {
                level = level,
                collectionRound = monsterCollectionRound,
            };

            IAccountStateDelta nextState = action.Execute(new ActionContext
            {
                PreviousStates = _initialState,
                Signer = _signer,
                BlockIndex = blockIndex,
            });

            MonsterCollectionState nextMonsterCollectionState = new MonsterCollectionState((Dictionary)nextState.GetState(monsterCollectionAddress));
            AgentState nextAgentState = nextState.GetAgentState(_signer);
            Assert.Equal(level, nextMonsterCollectionState.Level);
            Assert.Equal(0 * currency, nextState.GetBalance(_signer, currency));
            Assert.Equal(monsterCollectionRound, nextAgentState.MonsterCollectionRound);
            long rewardLevel = nextMonsterCollectionState.GetRewardLevel(blockIndex);
            for (long i = rewardLevel; i < 4; i++)
            {
                List<MonsterCollectionRewardSheet.RewardInfo> expected = _tableSheets.MonsterCollectionRewardSheet[level].Rewards;
                Assert.Equal(expected, nextMonsterCollectionState.RewardLevelMap[i + 1]);
            }
        }

        [Fact]
        public void Execute_Throw_FailedLoadStateException()
        {
            MonsterCollect action = new MonsterCollect
            {
                level = 1,
                collectionRound = 1,
            };

            Assert.Throws<FailedLoadStateException>(() => action.Execute(new ActionContext
            {
                PreviousStates = new State(),
                Signer = _signer,
                BlockIndex = 1,
            }));
        }

        [Theory]
        [InlineData(2, 1)]
        [InlineData(1, 2)]
        public void Execute_Throw_InvalidMonsterCollectionRoundException(int agentCollectionRound, int collectionRound)
        {
            AgentState prevAgentState = _initialState.GetAgentState(_signer);
            while (prevAgentState.MonsterCollectionRound < agentCollectionRound)
            {
                prevAgentState.IncreaseCollectionRound();
            }

            _initialState = _initialState.SetState(_signer, prevAgentState.Serialize());

            MonsterCollect action = new MonsterCollect
            {
                level = 1,
                collectionRound = collectionRound,
            };

            Assert.Throws<InvalidMonsterCollectionRoundException>(() => action.Execute(new ActionContext
            {
                PreviousStates = _initialState,
                Signer = _signer,
                BlockIndex = 1,
            }));
        }

        [Fact]
        public void Execute_Throw_SheetRowNotFoundException()
        {
            int level = 100;

            Assert.False(_tableSheets.MonsterCollectionSheet.Keys.Contains(level));

            MonsterCollect action = new MonsterCollect
            {
                level = level,
                collectionRound = 0,
            };

            Assert.Throws<SheetRowNotFoundException>(() => action.Execute(new ActionContext
            {
                PreviousStates = _initialState,
                Signer = _signer,
                BlockIndex = 1,
            }));
        }

        [Fact]
        public void Execute_Throw_InsufficientBalanceException()
        {
            MonsterCollect action = new MonsterCollect
            {
                level = 1,
            };

            Assert.Throws<InsufficientBalanceException>(() => action.Execute(new ActionContext
            {
                PreviousStates = _initialState,
                Signer = _signer,
                BlockIndex = 1,
            }));
        }

        [Fact]
        public void Execute_Throw_MonsterCollectionExpiredException()
        {
            Address collectionAddress = MonsterCollectionState.DeriveAddress(_signer, 0);
            MonsterCollectionState prevMonsterCollectionState = new MonsterCollectionState(collectionAddress, 1, 0, _tableSheets.MonsterCollectionRewardSheet);
            Assert.Equal(MonsterCollectionState.ExpirationIndex, prevMonsterCollectionState.ExpiredBlockIndex);

            _initialState = _initialState.SetState(collectionAddress, prevMonsterCollectionState.Serialize());

            MonsterCollect action = new MonsterCollect
            {
                level = 2,
                collectionRound = 0,
            };

            Assert.Throws<MonsterCollectionExpiredException>(() => action.Execute(new ActionContext
            {
                PreviousStates = _initialState,
                Signer = _signer,
                BlockIndex = prevMonsterCollectionState.ExpiredBlockIndex + 1,
            }));
        }

        [Theory]
        [InlineData(2, 1)]
        [InlineData(2, 2)]
        public void Execute_Throw_InvalidLevelException(int prevLevel, int level)
        {
            Address collectionAddress = MonsterCollectionState.DeriveAddress(_signer, 0);
            MonsterCollectionState prevMonsterCollectionState = new MonsterCollectionState(collectionAddress, prevLevel, 0, _tableSheets.MonsterCollectionRewardSheet);
            _initialState = _initialState.SetState(collectionAddress, prevMonsterCollectionState.Serialize());

            MonsterCollect action = new MonsterCollect
            {
                level = level,
                collectionRound = 0,
            };

            Assert.Throws<InvalidLevelException>(() => action.Execute(new ActionContext
            {
                PreviousStates = _initialState,
                Signer = _signer,
                BlockIndex = 1,
            }));
        }

        [Fact]
        public void Rehearsal()
        {
            Address collectionAddress = MonsterCollectionState.DeriveAddress(_signer, 1);
            MonsterCollect action = new MonsterCollect
            {
                level = 1,
                collectionRound = 1,
            };
            IAccountStateDelta nextState = action.Execute(new ActionContext
            {
                PreviousStates = new State(),
                Signer = _signer,
                Rehearsal = true,
            });

            List<Address> updatedAddresses = new List<Address>()
            {
                _signer,
                collectionAddress,
            };

            Assert.Equal(updatedAddresses.ToImmutableHashSet(), nextState.UpdatedAddresses);
        }
    }
}
