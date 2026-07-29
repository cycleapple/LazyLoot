using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.Logging;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Component.Exd;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using ECommons;

namespace LazyLoot;

internal static class Roller
{
    unsafe delegate bool RollItemRaw(Loot* lootIntPtr, RollResult option, uint lootItemIndex);

    static RollItemRaw _rollItemRaw;

    static uint _itemId = 0, _index = 0;

    public static void Clear()
    {
        _itemId = _index = 0;
    }

    public static bool RollOneItem(RollResult option, ref int need, ref int greed, ref int pass)
    {
        if (!GetNextLootItem(out var index, out var loot)) return false;

        // Make sure we ignore the fulf state if the user has custom item rules
        // Otherwise, we make sure the option is a valid roll
        var userRules = GetPlayerCustomRestrict(loot);
        option = userRules != null
            ? ResultMerge(GetRestrictResult(loot), (RollResult)userRules)
            : ResultMerge(option, GetRestrictResult(loot), GetPlayerRestrict(loot));

        if (_itemId == loot.ItemId && index == _index)
        {
            if (LazyLoot.Config.DiagnosticsMode && !LazyLoot.Config.NoPassEmergency)
                DuoLog.Debug(
                    $"{Svc.Data.GetExcelSheet<Item>().GetRow(loot.ItemId).Name} 因不明原因擲骰失敗，為安全起見將選擇放棄。[Emergency pass]");

            if (!LazyLoot.Config.NoPassEmergency)
            {
                switch (option)
                {
                    case RollResult.Needed:
                        need--;
                        break;
                    case RollResult.Greeded:
                        greed--;
                        break;
                    default:
                        pass--;
                        break;
                }

                option = RollResult.Passed;
            }
        }

        RollItem(option, index);
        _itemId = loot.ItemId;
        _index = index;

        switch (option)
        {
            case RollResult.Needed:
                need++;
                break;
            case RollResult.Greeded:
                greed++;
                break;
            default:
                pass++;
                break;
        }

        return true;
    }

    private static RollResult GetRestrictResult(LootItem loot)
    {
        var item = Svc.Data.GetExcelSheet<Item>().GetRowOrDefault(loot.ItemId);
        if (item == null)
            return RollResult.Passed;

        //Checks what the max possible roll type on the item is
        var stateMax = loot.RollState switch
        {
            RollState.UpToNeed => RollResult.Needed,
            RollState.UpToGreed => RollResult.Greeded,
            _ => RollResult.Passed,
        };

        if (item.Value.IsUnique && IsItemUnlocked(loot.ItemId))
            stateMax = RollResult.Passed;

        if (LazyLoot.Config.DiagnosticsMode && stateMax == RollResult.Passed)
            DuoLog.Debug($"{item.Value.Name} 目前只能選擇放棄。[RollState UpToPass]");

        //Checks what the player set loot rules are
        var ruleMax = loot.LootMode switch
        {
            LootMode.Normal => RollResult.Needed,
            LootMode.GreedOnly => RollResult.Greeded,
            _ => RollResult.Passed,
        };

        return ResultMerge(stateMax, ruleMax);
    }

    private static unsafe RollResult? GetPlayerCustomRestrict(LootItem loot)
    {
        var lootItem = Svc.Data.GetExcelSheet<Item>().GetRowOrDefault(loot.ItemId);
        if (lootItem == null || (lootItem.Value.IsUnique && ItemCount(loot.ItemId) > 0)) return RollResult.Passed;

        // Here, we will check for the specific rules for items.
        var itemCustomRestriction =
            LazyLoot.Config.Restrictions.Items.FirstOrDefault(x => x.Id == loot.ItemId);
        if (itemCustomRestriction is { Enabled: true })
        {
            if (LazyLoot.Config.DiagnosticsMode)
            {
                var action = itemCustomRestriction.RollRule == RollResult.Passed ? "放棄" :
                    itemCustomRestriction.RollRule == RollResult.Greeded ? "貪婪" :
                    itemCustomRestriction.RollRule == RollResult.Needed ? "需求" : "放棄";
                Svc.Log.Debug($"{lootItem.Value.Name} 將選擇{action}。[Item Custom Restriction]");
            }

            return itemCustomRestriction.RollRule;
        }

        // Here, we will check for the specific rules for the Duty.
        var contentFinderInfo = Svc.Data.GetExcelSheet<ContentFinderCondition>()
            .GetRow(GameMain.Instance()->CurrentContentFinderConditionId);
        var dutyCustomRestriction =
            LazyLoot.Config.Restrictions.Duties.FirstOrDefault(x => x.Id == contentFinderInfo.RowId);
        if (dutyCustomRestriction is { Enabled: true })
        {
            if (LazyLoot.Config.DiagnosticsMode)
            {
                var action = dutyCustomRestriction.RollRule == RollResult.Passed ? "放棄" :
                    dutyCustomRestriction.RollRule == RollResult.Greeded ? "貪婪" :
                    dutyCustomRestriction.RollRule == RollResult.Needed ? "需求" : "放棄";
                Svc.Log.Debug(
                    $"因目前位於「{contentFinderInfo.Name}」，{lootItem.Value.Name} 將選擇{action}。[Duty Custom Restriction]");
            }

            return dutyCustomRestriction.RollRule;
        }

        return null;
    }

    private static unsafe RollResult GetPlayerRestrict(LootItem loot)
    {
        var lootItem = Svc.Data.GetExcelSheet<Item>().GetRowOrDefault(loot.ItemId);

        UpdateFadedCopy(loot.ItemId, out var orchId);

        if (lootItem == null)
        {
            if (LazyLoot.Config.DiagnosticsMode)
                DuoLog.Debug(
                    $"因物品不明而選擇放棄。請將此 ID 提供給開發者：{loot.ItemId} [Unknown ID]");
            return RollResult.Passed;
        }

        if (lootItem.Value.IsUnique && ItemCount(loot.ItemId) > 0)
        {
            if (LazyLoot.Config.DiagnosticsMode)
                DuoLog.Debug(
                    $"{lootItem.Value.Name} 是唯一物品且你已持有，因此選擇放棄。[Unique Item]");

            return RollResult.Passed;
        }

        if (orchId.Count > 0 && orchId.All(x => IsItemUnlocked(x)))
        {
            if (LazyLoot.Config.RestrictionIgnoreItemUnlocked)
            {
                if (LazyLoot.Config.DiagnosticsMode)
                    DuoLog.Debug(
                        $"{lootItem.Value.Name} 已解鎖，且已啟用「放棄所有已解鎖物品」，因此選擇放棄。[Pass All Unlocked]");

                return RollResult.Passed;
            }

            if (LazyLoot.Config.RestrictionIgnoreFadedCopy
                && lootItem.Value.FilterGroup == 12 && lootItem.Value.ItemUICategory.RowId == 94)
            {
                if (LazyLoot.Config.DiagnosticsMode)
                    DuoLog.Debug(
                        $"{lootItem.Value.Name} 已解鎖，且已啟用「放棄已解鎖的褪色的樂譜」，因此選擇放棄。[Pass Faded Copies]");

                return RollResult.Passed;
            }
        }

        if (IsItemUnlocked(loot.ItemId))
        {
            if (LazyLoot.Config.RestrictionIgnoreItemUnlocked)
            {
                if (LazyLoot.Config.DiagnosticsMode)
                    DuoLog.Debug(
                        $"{lootItem.Value.Name} 已解鎖，且已啟用「放棄所有已解鎖物品」，因此選擇放棄。[Pass All Unlocked]");

                return RollResult.Passed;
            }

            if (LazyLoot.Config.RestrictionIgnoreMounts && lootItem.Value.ItemAction.Value.Type == 1322)
            {
                if (LazyLoot.Config.DiagnosticsMode)
                    DuoLog.Debug(
                        $"{lootItem.Value.Name} 已解鎖，且已啟用「放棄已解鎖的坐騎」，因此選擇放棄。[Pass Mounts]");

                return RollResult.Passed;
            }

            if (LazyLoot.Config.RestrictionIgnoreMinions && lootItem.Value.ItemAction.Value.Type == 853)
            {
                if (LazyLoot.Config.DiagnosticsMode)
                    DuoLog.Debug(
                        $"{lootItem.Value.Name} 已解鎖，且已啟用「放棄已解鎖的寵物」，因此選擇放棄。[Pass Minions]");

                return RollResult.Passed;
            }

            if (LazyLoot.Config.RestrictionIgnoreBardings
                && lootItem.Value.ItemAction.Value.Type == 1013)
            {
                if (LazyLoot.Config.DiagnosticsMode)
                    DuoLog.Debug(
                        $"{lootItem.Value.Name} 已解鎖，且已啟用「放棄已解鎖的陸行鳥裝甲」，因此選擇放棄。[Pass Bardings]");

                return RollResult.Passed;
            }

            if (LazyLoot.Config.RestrictionIgnoreEmoteHairstyle
                && lootItem.Value.ItemAction.Value.Type == 2633)
            {
                if (LazyLoot.Config.DiagnosticsMode)
                    DuoLog.Debug(
                        $"{lootItem.Value.Name} 已解鎖，且已啟用「放棄已解鎖的情感動作與髮型」，因此選擇放棄。[Pass Emotes/Hairstyles]");

                return RollResult.Passed;
            }

            if (LazyLoot.Config.RestrictionIgnoreTripleTriadCards
                && lootItem.Value.ItemAction.Value.Type == 3357)
            {
                if (LazyLoot.Config.DiagnosticsMode)
                    DuoLog.Debug(
                        $"{lootItem.Value.Name} 已解鎖，且已啟用「放棄已解鎖的九宮幻卡」，因此選擇放棄。[Pass TTCards]");

                return RollResult.Passed;
            }

            if (LazyLoot.Config.RestrictionIgnoreOrchestrionRolls
                && lootItem.Value.ItemAction.Value.Type == 25183)
            {
                if (LazyLoot.Config.DiagnosticsMode)
                    DuoLog.Debug(
                        $"{lootItem.Value.Name} 已解鎖，且已啟用「放棄已解鎖的樂譜」，因此選擇放棄。[Pass Orchestrion]");

                return RollResult.Passed;
            }
        }

        if (LazyLoot.Config.RestrictionSeals)
        {
            if (lootItem.Value.Rarity > 1 && lootItem.Value.PriceLow > 0 && lootItem.Value.ClassJobCategory.RowId > 0)
            {
                var gcSealValue = Svc.Data.Excel.GetSheet<GCSupplyDutyReward>()?.GetRow(lootItem.Value.LevelItem.RowId)
                    .SealsExpertDelivery;
                if (gcSealValue < LazyLoot.Config.RestrictionSealsAmnt)
                {
                    if (LazyLoot.Config.DiagnosticsMode)
                        DuoLog.Debug(
                            $"{lootItem.Value.Name} 的稀有品籌備軍票價值低於 {LazyLoot.Config.RestrictionSealsAmnt}，因此選擇放棄。[Pass Seals]");

                    return RollResult.Passed;
                }
            }
        }

        if (lootItem.Value.EquipSlotCategory.RowId != 0)
        {
            // Check if the loot item level is below the average level of the user job
            if (LazyLoot.Config.RestrictionLootLowerThanJobIlvl && loot.RollState == RollState.UpToNeed)
            {
                if (lootItem.Value.LevelItem.RowId <
                    Utils.GetPlayerIlevel() - LazyLoot.Config.RestrictionLootLowerThanJobIlvlTreshold)
                {
                    if (LazyLoot.Config.DiagnosticsMode &&
                        LazyLoot.Config.RestrictionLootLowerThanJobIlvlRollState != 0)
                        DuoLog.Debug(
                            $"{lootItem.Value.Name} 的品級低於目前職業平均品級，且相關規則設為放棄，因此選擇放棄。[Pass Item Lower Than Average iLevel]");

                    return LazyLoot.Config.RestrictionLootLowerThanJobIlvlRollState == 0
                        ? RollResult.Greeded
                        : RollResult.Passed;
                }
            }

            if (LazyLoot.Config.RestrictionIgnoreItemLevelBelow
                && lootItem.Value.LevelItem.RowId < LazyLoot.Config.RestrictionIgnoreItemLevelBelowValue)
            {
                if (LazyLoot.Config.DiagnosticsMode)
                    DuoLog.Debug(
                        $"{lootItem.Value.Name} 的品級 {lootItem.Value.LevelItem.RowId} 低於設定值 {LazyLoot.Config.RestrictionIgnoreItemLevelBelowValue}，因此選擇放棄。[Pass Item Level]");

                return RollResult.Passed;
            }

            // Check if the loot item is an actual upgrade to the user (has a higher ilvl)
            if (LazyLoot.Config.RestrictionLootIsJobUpgrade && loot.RollState == RollState.UpToNeed)
            {
                var lootItemSlot = lootItem.Value.EquipSlotCategory.RowId;
                var itemsToVerify = new List<uint>();
                InventoryContainer* equippedItems =
                    InventoryManager.Instance()->GetInventoryContainer(InventoryType.EquippedItems);
                for (int i = 0; i < equippedItems->Size; i++)
                {
                    InventoryItem* equippedItem = equippedItems->GetInventorySlot(i);
                    var equippedItemData = Svc.Data.GetExcelSheet<Item>().GetRowOrDefault(equippedItem->ItemId);
                    if (equippedItemData == null) continue;
                    if (equippedItemData.Value.EquipSlotCategory.RowId != lootItemSlot) continue;
                    // We gather all the iLvls of the equipped items in the same slot (if any)
                    itemsToVerify.Add(equippedItemData.Value.LevelItem.RowId);
                }

                // And we check if from the items gathered, if the lowest is higher than the droped item, we follow the rules defined by the user
                if (itemsToVerify.Count > 0 && itemsToVerify.Min() > lootItem.Value.LevelItem.RowId)
                {
                    if (LazyLoot.Config.DiagnosticsMode && LazyLoot.Config.RestrictionLootIsJobUpgradeRollState != 0)
                        DuoLog.Debug(
                            $"{lootItem.Value.Name} 的品級低於同部位已裝備物品，且相關規則設為放棄，因此選擇放棄。[Pass Item if equipped is of higher level]");

                    return LazyLoot.Config.RestrictionLootIsJobUpgradeRollState == 0
                        ? RollResult.Greeded
                        : RollResult.Passed;
                }
            }

            if (LazyLoot.Config.RestrictionOtherJobItems
                && loot.RollState != RollState.UpToNeed)
            {
                if (LazyLoot.Config.DiagnosticsMode)
                    DuoLog.Debug(
                        $"{lootItem.Value.Name} 無法由目前職業使用，且已啟用相關放棄規則，因此選擇放棄。[Pass Other Job]");

                return RollResult.Passed;
            }
        }

        //PLD set.
        if (LazyLoot.Config.RestrictionOtherJobItems
            && lootItem.Value.ItemAction.Value.Type == 29153
            && !(Player.Object?.ClassJob.RowId is 1 or 19))
        {
            if (LazyLoot.Config.DiagnosticsMode)
                DuoLog.Debug(
                    $"{lootItem.Value.Name} 無法由目前職業使用，且已啟用相關放棄規則，因此選擇放棄。[Pass Other Job (PLD Sets)]");

            return RollResult.Passed;
        }

        return RollResult.UnAwarded;
    }

    public static void UpdateFadedCopy(uint itemId, out List<uint> orchId)
    {
        orchId = new();
        var lumina = Svc.Data.GetExcelSheet<Item>().GetRowOrDefault(itemId);
        if (lumina != null)
        {
            if (lumina.Value.FilterGroup == 12 && lumina.Value.ItemUICategory.RowId == 94)
            {
                var recipe = Svc.Data.GetExcelSheet<Recipe>()
                    ?.Where(x => x.Ingredient.Any(y => y.RowId == lumina.Value.RowId)).Select(x => x.ItemResult.Value)
                    .FirstOrDefault();
                if (recipe != null)
                {
                    if (LazyLoot.Config.DiagnosticsMode)
                        DuoLog.Debug(
                            $"Updating Faded Copy {lumina.Value.Name} ({itemId}) to Non-Faded {recipe.Value.Name} ({recipe.Value.RowId})");
                    orchId.Add(recipe.Value.RowId);
                    return;
                }
            }
        }
    }

    private static RollResult ResultMerge(params RollResult[] results)
        => results.Max() switch
        {
            RollResult.Needed => RollResult.Needed,
            RollResult.Greeded => RollResult.Greeded,
            _ => RollResult.Passed,
        };


    private static unsafe bool GetNextLootItem(out uint i, out LootItem loot)
    {
        var span = Loot.Instance()->Items;
        for (i = 0; i < span.Length; i++)
        {
            loot = span[(int)i];

            if (loot.ItemId >= 1000000) loot.ItemId -= 1000000;
            if (loot.ChestObjectId is 0 or 0xE0000000) continue;
            if (loot.RollResult != RollResult.UnAwarded) continue;
            if (loot.RollState is RollState.Rolled or RollState.Unavailable or RollState.Unknown) continue;
            if (loot.ItemId == 0) continue;
            if (loot.LootMode is LootMode.LootMasterGreedOnly or LootMode.Unavailable) continue;

            var checkWeekly = LazyLoot.Config.RestrictionWeeklyLockoutItems;

            var lootId = loot.ItemId;
            var contentFinderInfo = Svc.Data.GetExcelSheet<ContentFinderCondition>()
                .GetRow(GameMain.Instance()->CurrentContentFinderConditionId);

            // We load the users restrictions
            var itemCustomRestriction =
                LazyLoot.Config.Restrictions.Items.FirstOrDefault(x =>
                    x.Id == lootId && x is { Enabled: true });
            var dutyCustomRestriction =
                LazyLoot.Config.Restrictions.Duties.FirstOrDefault(x =>
                    x.Id == contentFinderInfo.RowId && x is { Enabled: true, RollRule: RollResult.UnAwarded });

            Item? item = null;

            if (LazyLoot.Config.DiagnosticsMode)
                // Only load the item if diagnostic mode is on
                item = Svc.Data.GetExcelSheet<Item>().GetRow(loot.ItemId);

            if (itemCustomRestriction != null)
            {
                if (itemCustomRestriction.RollRule == RollResult.UnAwarded)
                {
                    if (LazyLoot.Config.DiagnosticsMode)
                        DuoLog.Debug(
                            $"{item?.Name.ToString()} is being ignored. [Item Custom Restriction]");
                    continue;
                }

                checkWeekly = false;
            }

            if (itemCustomRestriction == null && dutyCustomRestriction != null)
            {
                if (dutyCustomRestriction.RollRule == RollResult.UnAwarded)
                {
                    if (LazyLoot.Config.DiagnosticsMode)
                        DuoLog.Debug(
                            $"{item?.Name.ToString()} is being ignored due to being in {contentFinderInfo.Name}. [Duty Custom Restriction]");
                    continue;
                }

                checkWeekly = false;
            }

            // loot.RollValue == 20 means it cant be rolled because one was already obtained this week.
            // we ignore that so it will be passed automatically, as there is nothing the user can do other than
            // pass it
            if (loot.WeeklyLootItem && (byte)loot.RollState != 20 && checkWeekly) continue;

            return true;
        }

        loot = default;
        return false;
    }

    private static unsafe void RollItem(RollResult option, uint index)
    {
        try
        {
            _rollItemRaw ??=
                Marshal.GetDelegateForFunctionPointer<RollItemRaw>(
                    Svc.SigScanner.ScanText("41 83 F8 ?? 0F 83 ?? ?? ?? ?? 48 89 5C 24 08"));
            _rollItemRaw?.Invoke(Loot.Instance(), option, index);
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "Warning at roll");
        }
    }

    public static unsafe int ItemCount(uint itemId)
        => InventoryManager.Instance()->GetInventoryItemCount(itemId);

    public static unsafe bool IsItemUnlocked(uint itemId)
    {
        var exdItem = ExdModule.GetItemRowById(itemId);
        return exdItem is null || UIState.Instance()->IsItemActionUnlocked(exdItem) is 1;
    }

    public static uint ConvertSealsToIlvl(int sealAmnt)
    {
        var sealsSheet = Svc.Data.GetExcelSheet<GCSupplyDutyReward>();
        uint ilvl = 0;
        foreach (var row in sealsSheet)
        {
            if (row.SealsExpertDelivery < sealAmnt)
            {
                ilvl = row.RowId;
            }
        }

        return ilvl;
    }
}
