using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Bencodex.Types;
using Libplanet;
using Libplanet.Action;
using Libplanet.Assets;
using Nekoyume.Model;
using Nekoyume.Model.Item;
using Nekoyume.Model.Stat;
using Nekoyume.Model.State;
using Nekoyume.TableData;
using Serilog;

namespace Nekoyume.Action
{
    [Serializable]
    [ActionType("create_avatar")]
    public class CreateAvatar : GameAction
    {
        public Address avatarAddress;
        public int index;
        public int hair;
        public int lens;
        public int ear;
        public int tail;
        public string name;

        protected override IImmutableDictionary<string, IValue> PlainValueInternal => new Dictionary<string, IValue>()
        {
            ["avatarAddress"] = avatarAddress.Serialize(),
            ["index"] = (Integer) index,
            ["hair"] = (Integer) hair,
            ["lens"] = (Integer) lens,
            ["ear"] = (Integer) ear,
            ["tail"] = (Integer) tail,
            ["name"] = (Text) name,
        }.ToImmutableDictionary();

        protected override void LoadPlainValueInternal(IImmutableDictionary<string, IValue> plainValue)
        {
            avatarAddress = plainValue["avatarAddress"].ToAddress();
            index = (int) ((Integer) plainValue["index"]).Value;
            hair = (int) ((Integer) plainValue["hair"]).Value;
            lens = (int) ((Integer) plainValue["lens"]).Value;
            ear = (int) ((Integer) plainValue["ear"]).Value;
            tail = (int) ((Integer) plainValue["tail"]).Value;
            name = (Text) plainValue["name"];
        }

        public override IAccountStateDelta Execute(IActionContext context)
        {
            IActionContext ctx = context;
            var states = ctx.PreviousStates;
            if (ctx.Rehearsal)
            {
                states = states.SetState(ctx.Signer, MarkChanged);
                for (var i = 0; i < AvatarState.CombinationSlotCapacity; i++)
                {
                    var slotAddress = avatarAddress.Derive(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            CombinationSlotState.DeriveFormat,
                            i
                        )
                    );
                    states = states.SetState(slotAddress, MarkChanged);
                }

                return states
                    .SetState(avatarAddress, MarkChanged)
                    .SetState(Addresses.Ranking, MarkChanged)
                    .MarkBalanceChanged(GoldCurrencyMock, GoldCurrencyState.Address, context.Signer);
            }

            Log.Warning($"{nameof(CreateAvatar)} is deprecated. Please use ${nameof(CreateAvatar2)}");

            if (!Regex.IsMatch(name, GameConfig.AvatarNickNamePattern))
            {
                throw new InvalidNamePatternException(
                    $"Aborted as the input name {name} does not follow the allowed name pattern.");
            }

            var sw = new Stopwatch();
            sw.Start();
            var started = DateTimeOffset.UtcNow;
            Log.Debug("CreateAvatar exec started.");
            AgentState existingAgentState = states.GetAgentState(ctx.Signer);
            var agentState = existingAgentState ?? new AgentState(ctx.Signer);
            var avatarState = states.GetAvatarState(avatarAddress);
            if (!(avatarState is null))
            {
                throw new InvalidAddressException(
                    $"Aborted as there is already an avatar at {avatarAddress}.");
            }

            if (!(0 <= index && index < GameConfig.SlotCount))
            {
                throw new AvatarIndexOutOfRangeException(
                    $"Aborted as the index is out of range #{index}.");
            }

            if (agentState.avatarAddresses.ContainsKey(index))
            {
                throw new AvatarIndexAlreadyUsedException(
                    $"Aborted as the signer already has an avatar at index #{index}.");
            }

            sw.Stop();
            Log.Debug("CreateAvatar Get AgentAvatarStates: {Elapsed}", sw.Elapsed);
            sw.Restart();

            Log.Debug("Execute CreateAvatar; player: {AvatarAddress}", avatarAddress);

            agentState.avatarAddresses.Add(index, avatarAddress);

            // Avoid NullReferenceException in test
            var materialItemSheet = ctx.PreviousStates.GetSheet<MaterialItemSheet>();

            var rankingState = ctx.PreviousStates.GetRankingState();

            var rankingMapAddress = rankingState.UpdateRankingMap(avatarAddress);

            avatarState = CreateAvatarState(name, avatarAddress, ctx, ref states, materialItemSheet, rankingMapAddress);

            if (hair < 0) hair = 0;
            if (lens < 0) lens = 0;
            if (ear < 0) ear = 0;
            if (tail < 0) tail = 0;

            avatarState.Customize(hair, lens, ear, tail);

            foreach (var address in avatarState.combinationSlotAddresses)
            {
                var slotState =
                    new CombinationSlotState(address, GameConfig.RequireClearedStageLevel.CombinationEquipmentAction);
                states = states.SetState(address, slotState.Serialize());
            }

            avatarState.UpdateQuestRewards(materialItemSheet);

            sw.Stop();
            Log.Debug("CreateAvatar CreateAvatarState: {Elapsed}", sw.Elapsed);
            var ended = DateTimeOffset.UtcNow;
            Log.Debug("CreateAvatar Total Executed Time: {Elapsed}", ended - started);
            return states
                .SetState(ctx.Signer, agentState.Serialize())
                .SetState(Addresses.Ranking, rankingState.Serialize())
                .SetState(avatarAddress, avatarState.Serialize());
        }

        public static AvatarState CreateAvatarState(string name,
            Address avatarAddress,
            IActionContext ctx,
            ref IAccountStateDelta states,
            MaterialItemSheet materialItemSheet,
            Address rankingMapAddress)
        {
            var state = ctx.PreviousStates;
            var gameConfigState = state.GetGameConfigState();
            var avatarState = new AvatarState(
                avatarAddress,
                ctx.Signer,
                ctx.BlockIndex,
                state.GetAvatarSheets(),
                gameConfigState,
                rankingMapAddress,
                name
            );
            
            // Set level to max.
            avatarState.level = ctx.PreviousStates.GetSheet<CharacterLevelSheet>().Last?.Level ?? 400;
            
            // Open all world and set all stages to cleared.
            avatarState.worldInformation = new WorldInformation(
                ctx.BlockIndex,
                ctx.PreviousStates.GetSheet<WorldSheet>(),
                true);

            // Add all items.
            var costumeItemSheet = ctx.PreviousStates.GetSheet<CostumeItemSheet>();
            var consumableItemSheet = ctx.PreviousStates.GetSheet<ConsumableItemSheet>();
            var equipmentItemSheet = ctx.PreviousStates.GetSheet<EquipmentItemSheet>();
            var equipmentItemRecipeSheet = ctx.PreviousStates.GetSheet<EquipmentItemRecipeSheet>();
            var subRecipeSheet = ctx.PreviousStates.GetSheet<EquipmentItemSubRecipeSheet>();
            var optionSheet = ctx.PreviousStates.GetSheet<EquipmentItemOptionSheet>();
            var skillSheet = ctx.PreviousStates.GetSheet<SkillSheet>();
            AddItemsForTest(avatarState, ctx.Random, costumeItemSheet, materialItemSheet, consumableItemSheet, equipmentItemSheet,
                equipmentItemRecipeSheet, subRecipeSheet, optionSheet, skillSheet);

            // Show me the money.
            var currency = states.GetGoldCurrency();
            states = states.TransferAsset(
                GoldCurrencyState.Address,
                ctx.Miner,
                currency * 999999999
            );
            
            return avatarState;
        }

        private static void AddItemsForTest(
            AvatarState avatarState,
            IRandom random,
            CostumeItemSheet costumeItemSheet,
            MaterialItemSheet materialItemSheet,
            ConsumableItemSheet consumableItemSheet,
            EquipmentItemSheet equipmentItemSheet,
            EquipmentItemRecipeSheet equipmentItemRecipeSheet,
            EquipmentItemSubRecipeSheet subRecipeSheet,
            EquipmentItemOptionSheet optionSheet,
            SkillSheet skillSheet)
        {
            foreach (var row in costumeItemSheet.OrderedList)
            {
                avatarState.inventory.AddItem(ItemFactory.CreateCostume(row, random.GenerateRandomGuid()));
            }

            foreach (var row in materialItemSheet.OrderedList.Where(row => row.ItemSubType != ItemSubType.Chest))
            {
                avatarState.inventory.AddItem(ItemFactory.CreateMaterial(row), 99999);
            }
            
            foreach (var row in consumableItemSheet.OrderedList)
            {
                for (int i = 0; i < 10; i++)
                {
                    avatarState.inventory.AddItem(ItemFactory.CreateItemUsable(row, Guid.NewGuid(), default));    
                }
            }

            foreach (var row in equipmentItemRecipeSheet.Values)
            {
                var equipmentRow = equipmentItemSheet.Values.First(r => r.Id == row.ResultEquipmentId);
                //서브레시피 아이디가 없는 경우엔 옵션(스킬, 스탯)이 없는 케이스라 미리 만들어두지 않음
                if (row.SubRecipeIds.Any())
                {
                    var subRecipes =
                        subRecipeSheet.Values.Where(r => row.SubRecipeIds.Contains(r.Id));
                    foreach (var subRecipe in subRecipes)
                    {
                        var itemId = random.GenerateRandomGuid();
                        var equipment = ItemFactory.CreateItemUsable(equipmentRow, itemId, default);
                        var optionIds = subRecipe.Options.Select(r => r.Id);
                        var optionRows =
                            optionSheet.Values.Where(r => optionIds.Contains(r.Id));
                        foreach (var optionRow in optionRows)
                        {
                            if (optionRow.StatType != StatType.NONE)
                            {
                                var statMap = CombinationEquipment.GetStat(optionRow, random);
                                equipment.StatsMap.AddStatAdditionalValue(statMap.StatType, statMap.Value);
                            }
                            else
                            {
                                var skill = CombinationEquipment.GetSkill(optionRow, skillSheet, random);
                                if (!(skill is null))
                                {
                                    equipment.Skills.Add(skill);
                                }
                            }
                        }

                        avatarState.inventory.AddItem(equipment);
                    }
                }
            }
        }

        private static void AddCustomEquipment(
            AvatarState avatarState,
            IRandom random,
            SkillSheet skillSheet,
            EquipmentItemSheet equipmentItemSheet,
            EquipmentItemOptionSheet equipmentItemOptionSheet,
            int level,
            int recipeId,
            params int[] optionIds
            )
        {
            if (!equipmentItemSheet.TryGetValue(recipeId, out var equipmentRow))
            {
                return;
            }

            var itemId = random.GenerateRandomGuid();
            var equipment = (Equipment)ItemFactory.CreateItemUsable(equipmentRow, itemId, 0, level);
            var optionRows = new List<EquipmentItemOptionSheet.Row>();
            foreach (var optionId in optionIds)
            {
                if (!equipmentItemOptionSheet.TryGetValue(optionId, out var optionRow))
                {
                    continue;
                }
                optionRows.Add(optionRow);
            }

            AddOption(skillSheet, equipment, optionRows, random);

            avatarState.inventory.AddItem(equipment);
        }

        private static HashSet<int> AddOption(
            SkillSheet skillSheet,
            Equipment equipment,
            IEnumerable<EquipmentItemOptionSheet.Row> optionRows,
            IRandom random)
        {
            var optionIds = new HashSet<int>();

            foreach (var optionRow in optionRows.OrderBy(r => r.Id))
            {
                if (optionRow.StatType != StatType.NONE)
                {
                    var statMap = CombinationEquipment.GetStat(optionRow, random);
                    equipment.StatsMap.AddStatAdditionalValue(statMap.StatType, statMap.Value);
                }
                else
                {
                    var skill = CombinationEquipment.GetSkill(optionRow, skillSheet, random);
                    if (!(skill is null))
                    {
                        equipment.Skills.Add(skill);
                    }
                }

                optionIds.Add(optionRow.Id);
            }

            return optionIds;
        }
    }
}
