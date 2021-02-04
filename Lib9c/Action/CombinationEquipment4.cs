using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Bencodex.Types;
using Libplanet;
using Libplanet.Action;
using Libplanet.Assets;
using Nekoyume.Battle;
using Nekoyume.Model.Item;
using Nekoyume.Model.Mail;
using Nekoyume.Model.Skill;
using Nekoyume.Model.Stat;
using Nekoyume.Model.State;
using Nekoyume.TableData;
using Serilog;

namespace Nekoyume.Action
{
    [Serializable]
    [ActionType("combination_equipment4")]
    public class CombinationEquipment4 : GameAction
    {
        public static readonly Address BlacksmithAddress = ItemEnhancement.BlacksmithAddress;

        public Address AvatarAddress;
        public int RecipeId;
        public int SlotIndex;
        public int? SubRecipeId;

        public override IAccountStateDelta Execute(IActionContext context)
        {
            IActionContext ctx = context;
            var states = ctx.PreviousStates;
            var slotAddress = AvatarAddress.Derive(
                string.Format(
                    CultureInfo.InvariantCulture,
                    CombinationSlotState.DeriveFormat,
                    SlotIndex
                )
            );
            if (ctx.Rehearsal)
            {
                return states
                    .SetState(AvatarAddress, MarkChanged)
                    .SetState(slotAddress, MarkChanged)
                    .SetState(ctx.Signer, MarkChanged)
                    .MarkBalanceChanged(GoldCurrencyMock, ctx.Signer, BlacksmithAddress);
            }
            
            var addressesHex = GetSignerAndOtherAddressesHex(context, AvatarAddress);

            if (!states.TryGetAgentAvatarStates(ctx.Signer, AvatarAddress, out var agentState,
                out var avatarState))
            {
                var exc = new FailedLoadStateException($"{addressesHex}Aborted as the avatar state of the signer was failed to load.");
                Log.Error(exc.Message);
                throw exc;
            }

            var slotState = states.GetCombinationSlotState(AvatarAddress, SlotIndex);
            if (slotState is null)
            {
                var exc = new FailedLoadStateException($"{addressesHex}Aborted as the slot state is failed to load");
                Log.Error(exc.Message);
                throw exc;
            }

            if (!slotState.Validate(avatarState, ctx.BlockIndex))
            {
                var exc = new CombinationSlotUnlockException(
                    $"{addressesHex}Aborted as the slot state is invalid: {slotState} @ {SlotIndex}");
                Log.Error(exc.Message);
                throw exc;
            }

            var recipeSheet = states.GetSheet<EquipmentItemRecipeSheet>();
            var materialSheet = states.GetSheet<MaterialItemSheet>();
            var materials = new Dictionary<Material, int>();

            // Validate recipe.
            if (!recipeSheet.TryGetValue(RecipeId, out var recipe))
            {
                var exc = new SheetRowNotFoundException(addressesHex, nameof(EquipmentItemRecipeSheet), RecipeId);
                Log.Error(exc.Message);
                throw exc;
            }

            if (!(SubRecipeId is null))
            {
                if (!recipe.SubRecipeIds.Contains((int) SubRecipeId))
                {
                    var exc = new SheetRowColumnException(
                        $"{addressesHex}Aborted as the sub recipe {SubRecipeId} was failed to load from the sheet."
                    );
                    Log.Error(exc.Message);
                    throw exc;
                }
            }

            // Validate main recipe is unlocked.
            if (!avatarState.worldInformation.IsStageCleared(recipe.UnlockStage))
            {
                avatarState.worldInformation.TryGetLastClearedStageId(out var current);
                var exc = new NotEnoughClearedStageLevelException(addressesHex, recipe.UnlockStage, current);
                Log.Error(exc.Message);
                throw exc;
            }

            if (!materialSheet.TryGetValue(recipe.MaterialId, out var material))
            {
                var exc = new SheetRowNotFoundException(addressesHex, nameof(MaterialItemSheet), recipe.MaterialId);
                Log.Error(exc.Message);
                throw exc;
            }

            if (!avatarState.inventory.RemoveMaterial(material.ItemId, recipe.MaterialCount))
            {
                var exc = new NotEnoughMaterialException(
                    $"{addressesHex}Aborted as the player has no enough material ({material} * {recipe.MaterialCount})"
                );
                Log.Error(exc.Message);
                throw exc;
            }

            var equipmentMaterial = ItemFactory.CreateMaterial(materialSheet, material.Id);
            materials[equipmentMaterial] = recipe.MaterialCount;

            BigInteger requiredGold = recipe.RequiredGold;
            var requiredActionPoint = recipe.RequiredActionPoint;
            var equipmentItemSheet = states.GetSheet<EquipmentItemSheet>();

            // Validate equipment id.
            if (!equipmentItemSheet.TryGetValue(recipe.ResultEquipmentId, out var equipRow))
            {
                var exc = new SheetRowNotFoundException(addressesHex, nameof(equipmentItemSheet), recipe.ResultEquipmentId);
                Log.Error(exc.Message);
                throw exc;
            }

            var requiredBlockIndex = ctx.BlockIndex + recipe.RequiredBlockIndex;
            var equipment = (Equipment) ItemFactory.CreateItemUsable(
                equipRow,
                ctx.Random.GenerateRandomGuid(),
                requiredBlockIndex
            );

            // Validate sub recipe.
            HashSet<int> optionIds = null;
            if (SubRecipeId.HasValue)
            {
                var subSheet = states.GetSheet<EquipmentItemSubRecipeSheet>();
                var subId = (int) SubRecipeId;
                if (!subSheet.TryGetValue(subId, out var subRecipe))
                {
                    var exc = new SheetRowNotFoundException(addressesHex, nameof(EquipmentItemSubRecipeSheet), subId);
                    Log.Error(exc.Message);
                    throw exc;
                }

                requiredBlockIndex += subRecipe.RequiredBlockIndex;
                requiredGold += subRecipe.RequiredGold;
                requiredActionPoint += subRecipe.RequiredActionPoint;

                foreach (var materialInfo in subRecipe.Materials)
                {
                    if (!materialSheet.TryGetValue(materialInfo.Id, out var subMaterialRow))
                    {
                        var exc = new SheetRowNotFoundException(addressesHex, nameof(MaterialItemSheet), materialInfo.Id);
                        Log.Error(exc.Message);
                        throw exc;
                    }

                    if (!avatarState.inventory.RemoveMaterial(subMaterialRow.ItemId,
                        materialInfo.Count))
                    {
                        var exc = new NotEnoughMaterialException(
                            $"{addressesHex}Aborted as the player has no enough material ({subMaterialRow} * {materialInfo.Count})"
                        );
                        Log.Error(exc.Message);
                        throw exc;
                    }

                    var subMaterial = ItemFactory.CreateMaterial(materialSheet, materialInfo.Id);
                    materials[subMaterial] = materialInfo.Count;
                }

                optionIds = SelectOption(states.GetSheet<EquipmentItemOptionSheet>(), states.GetSheet<SkillSheet>(),
                    subRecipe, ctx.Random, equipment);
                equipment.Update(requiredBlockIndex);
            }

            // Validate NCG.
            FungibleAssetValue agentBalance = states.GetBalance(ctx.Signer, states.GetGoldCurrency());
            if (agentBalance < states.GetGoldCurrency() * requiredGold)
            {
                var exc = new InsufficientBalanceException(
                    ctx.Signer,
                    agentBalance,
                    $"{addressesHex}Aborted as the agent ({ctx.Signer}) has no sufficient gold: {agentBalance} < {requiredGold}"
                );
                Log.Error(exc.Message);
                throw exc;
            }

            if (avatarState.actionPoint < requiredActionPoint)
            {
                var exc = new NotEnoughActionPointException(
                    $"{addressesHex}Aborted due to insufficient action point: {avatarState.actionPoint} < {requiredActionPoint}"
                );
                Log.Error(exc.Message);
                throw exc;
            }

            avatarState.actionPoint -= requiredActionPoint;
            if (!(optionIds is null))
            {
                foreach (var id in optionIds.OrderBy(id => id))
                {
                    agentState.unlockedOptions.Add(id);
                }
            }

            // FIXME: BlacksmithAddress just accumulate NCG. we need plan how to circulate this.
            if (requiredGold > 0)
            {
                states = states.TransferAsset(
                    ctx.Signer,
                    BlacksmithAddress,
                    states.GetGoldCurrency() * requiredGold
                );
            }

            var result = new CombinationConsumable.ResultModel
            {
                actionPoint = requiredActionPoint,
                gold = requiredGold,
                materials = materials,
                itemUsable = equipment,
                recipeId = RecipeId,
                subRecipeId = SubRecipeId,
                itemType = ItemType.Equipment,
            };
            slotState.Update(result, ctx.BlockIndex, requiredBlockIndex);
            var mail = new CombinationMail(result, ctx.BlockIndex, ctx.Random.GenerateRandomGuid(),
                requiredBlockIndex);
            result.id = mail.id;
            avatarState.UpdateV3(mail);
            avatarState.questList.UpdateCombinationEquipmentQuest(RecipeId);
            avatarState.UpdateFromCombination(equipment);
            avatarState.UpdateQuestRewards(materialSheet);
            return states
                .SetState(AvatarAddress, avatarState.Serialize())
                .SetState(slotAddress, slotState.Serialize())
                .SetState(ctx.Signer, agentState.Serialize());
        }

        protected override IImmutableDictionary<string, IValue> PlainValueInternal =>
            new Dictionary<string, IValue>
            {
                ["avatarAddress"] = AvatarAddress.Serialize(),
                ["recipeId"] = RecipeId.Serialize(),
                ["subRecipeId"] = SubRecipeId.Serialize(),
                ["slotIndex"] = SlotIndex.Serialize(),
            }.ToImmutableDictionary();

        protected override void LoadPlainValueInternal(
            IImmutableDictionary<string, IValue> plainValue)
        {
            AvatarAddress = plainValue["avatarAddress"].ToAddress();
            RecipeId = plainValue["recipeId"].ToInteger();
            SubRecipeId = plainValue["subRecipeId"].ToNullableInteger();
            SlotIndex = plainValue["slotIndex"].ToInteger();
        }

        private static StatMap GetStat(EquipmentItemOptionSheet.Row row, IRandom random)
        {
            var value = random.Next(row.StatMin, row.StatMax + 1);
            return new StatMap(row.StatType, value);
        }

        private static Skill GetSkill(EquipmentItemOptionSheet.Row row, SkillSheet skillSheet,
            IRandom random)
        {
            try
            {
                var skillRow = skillSheet.OrderedList.First(r => r.Id == row.SkillId);
                var dmg = random.Next(row.SkillDamageMin, row.SkillDamageMax + 1);
                var chance = random.Next(row.SkillChanceMin, row.SkillChanceMax + 1);
                var skill = SkillFactory.Get(skillRow, dmg, chance);
                return skill;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        public static HashSet<int> SelectOption(
            EquipmentItemOptionSheet optionSheet,
            SkillSheet skillSheet,
            EquipmentItemSubRecipeSheet.Row subRecipe,
            IRandom random,
            Equipment equipment
        )
        {
            var optionSelector = new WeightedSelector<EquipmentItemOptionSheet.Row>(random);
            var optionIds = new HashSet<int>();

            // Skip sort subRecipe.Options because it had been already sorted in WeightedSelector.Select();
            foreach (var optionInfo in subRecipe.Options)
            {
                if (!optionSheet.TryGetValue(optionInfo.Id, out var optionRow))
                {
                    continue;
                }

                optionSelector.Add(optionRow, optionInfo.Ratio);
            }

            IEnumerable<EquipmentItemOptionSheet.Row> optionRows =
                new EquipmentItemOptionSheet.Row[0];
            try
            {
                optionRows = optionSelector.SelectV3(subRecipe.MaxOptionLimit);
            }
            catch (Exception e) when (
                e is InvalidCountException ||
                e is ListEmptyException
            )
            {
                return optionIds;
            }
            finally
            {
                foreach (var optionRow in optionRows.OrderBy(r => r.Id))
                {
                    if (optionRow.StatType != StatType.NONE)
                    {
                        var statMap = GetStat(optionRow, random);
                        equipment.StatsMap.AddStatAdditionalValue(statMap.StatType, statMap.Value);
                    }
                    else
                    {
                        var skill = GetSkill(optionRow, skillSheet, random);
                        if (!(skill is null))
                        {
                            equipment.Skills.Add(skill);
                        }
                    }

                    optionIds.Add(optionRow.Id);
                }
            }

            return optionIds;
        }
    }
}
