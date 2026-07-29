using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Windowing;
using ECommons;
using ECommons.DalamudServices;
using ECommons.ImGuiMethods;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using PunishLib.ImGuiMethods;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace LazyLoot;

public class ConfigUi : Window, IDisposable
{
    private static List<CustomRestriction> importedRestrictions;
    private static int debugValue;
    private static string searchResultsQuery;
    private static double lastSearchTime;
    private static Item[] itemSearchResults;
    private static ContentFinderCondition[] dutySearchResults;

    internal WindowSystem windowSystem = new();

    [StructLayout(LayoutKind.Explicit, Size = 0x40)]
    public struct DebugLootItem
    {
        [FieldOffset(0x00)] public uint ChestObjectId;
        [FieldOffset(0x04)] public uint ChestItemIndex; // This loot item's index in the chest it came from
        [FieldOffset(0x08)] public uint ItemId;
        [FieldOffset(0x0C)] public ushort ItemCount;

        [FieldOffset(0x1C)] public uint GlamourItemId;
        [FieldOffset(0x20)] public RollState RollState;
        [FieldOffset(0x24)] public RollResult RollResult;
        [FieldOffset(0x28)] public byte RollValue;
        [FieldOffset(0x34)] public byte Unk1;
        [FieldOffset(0x38)] public byte Unk2;
        [FieldOffset(0x2C)] public float Time;
        [FieldOffset(0x30)] public float MaxTime;

        [FieldOffset(0x38)] public LootMode LootMode;
    }

    public ConfigUi() : base("Lazy Loot 設定")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(400, 200),
            MaximumSize = new Vector2(99999, 99999)
        };
        windowSystem.AddWindow(this);
        Svc.PluginInterface.UiBuilder.Draw += windowSystem.Draw;
    }

    public void Dispose()
    {
        Svc.PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        GC.SuppressFinalize(this);
    }

    public override void Draw()
    {
        if (ImGui.BeginTabBar("config"))
        {
            if (ImGui.BeginTabItem("功能"))
            {
                DrawFeatures();
                ImGui.Separator();
                DrawRollingDelay();
                ImGui.Separator();
                DrawChatAndToast();
                ImGui.Separator();
                DrawFulf();
                ImGui.Separator();
                DrawDiagnostics();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("自訂規則"))
            {
                DrawUserRestriction();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("關於"))
            {
                AboutTab.Draw("LazyLoot");
                ImGui.EndTabItem();
            }

#if DEBUG
            if (ImGui.BeginTabItem("偵錯"))
            {
                DrawDebug();
                ImGui.EndTabItem();
            }
#endif

            ImGui.EndTabBar();
        }
    }

    private static IDalamudTextureWrap? GetItemIcon(uint id)
    {
        return Svc.Texture.GetFromGameIcon(new GameIconLookup
        {
            IconId = id
        }).GetWrapOrDefault();
    }

    private unsafe void DrawDebug()
    {
        if (ImGui.CollapsingHeader("物品是否已解鎖？"))
        {
            ImGui.InputInt("偵錯數值測試", ref debugValue);
            ImGui.Text($"已解鎖：{Roller.IsItemUnlocked((uint)debugValue)}");
        }

        if (ImGui.CollapsingHeader("戰利品"))
        {
            var loot = Loot.Instance();
            if (loot != null)
            {
                foreach (var item in loot->Items)
                {
                    if (item.ItemId == 0) continue;
                    var casted = (DebugLootItem*)&item;
                    ImGui.PushID($"{casted->ItemId}");
                    Dalamud.Utility.Util.ShowStruct(casted);
                }
            }
        }

        //if (ImGui.Button("Faded Copy Converter Check?"))
        //{
        //    Roller.UpdateFadedCopy((uint)debugValue, out uint nonfaded);
        //    Svc.Log.Debug($"Non-Faded is {nonfaded}");\
        //}

        //if (ImGui.Button("Check all Faded Copies"))
        //{
        //    foreach (var i in Svc.Data.GetExcelSheet<Item>().Where(x => x.FilterGroup == 12 && x.ItemUICategory.Row == 94))
        //    {
        //        Roller.UpdateFadedCopy((uint)i.RowId, out uint nonfaded);
        //        Svc.Log.Debug($"{i.Name}");
        //    }
        //}
    }

    private void DrawDiagnostics()
    {
        ImGuiEx.LineCentered("DiagnosticsLabel", () => ImGuiEx.TextUnderlined("診斷與疑難排解"));

        if (ImGui.Checkbox("診斷模式", ref LazyLoot.Config.DiagnosticsMode))
            LazyLoot.Config.Save();

        ImGuiComponents.HelpMarker(
            "每次放棄物品時，會在聊天視窗輸出額外訊息與原因，方便與開發者診斷問題，或了解 LazyLoot 為何決定放棄該物品。\r\n\r\n這些訊息只會顯示給你，遊戲內其他玩家看不到。");

        if (ImGui.Checkbox("擲骰失敗時不要自動放棄物品", ref LazyLoot.Config.NoPassEmergency))
            LazyLoot.Config.Save();

        ImGuiComponents.HelpMarker(
            "通常 LazyLoot 會放棄擲骰失敗的物品。啟用此選項可避免這種情況，但可能產生非預期副作用，僅建議在遇到緊急放棄問題時使用。");
    }

    public override void OnClose()
    {
        LazyLoot.Config.Save();
        Notify.Success("設定已儲存");
        base.OnClose();
    }

    private static void DrawFeatures()
    {
        ImGuiEx.ImGuiLineCentered("FeaturesLabel", () => ImGuiEx.TextUnderlined("LazyLoot 擲骰指令"));
        ImGui.Columns(2, ImU8String.Empty, false);
        ImGui.SetColumnWidth(0, 80);
        ImGui.Text("/lazy need");
        ImGui.NextColumn();
        ImGui.Text("對所有物品選擇需求；無法需求時選擇貪婪，若也無法貪婪則放棄。");
        ImGui.NextColumn();
        ImGui.Text("/lazy greed");
        ImGui.NextColumn();
        ImGui.Text("對所有物品選擇貪婪；無法貪婪時選擇放棄。");
        ImGui.NextColumn();
        ImGui.Text("/lazy pass");
        ImGui.NextColumn();
        ImGui.Text("放棄所有尚未擲骰的物品。");
        ImGui.NextColumn();
        ImGui.Columns(1);
    }

    private static void DrawRollingDelay()
    {
        ImGuiEx.LineCentered("RollingDelayLabel", () => ImGuiEx.TextUnderlined("擲骰指令延遲"));
        ImGui.SetNextItemWidth(100);

        if (ImGui.DragFloatRange2("物品之間的擲骰延遲", ref LazyLoot.Config.MinRollDelayInSeconds,
                ref LazyLoot.Config.MaxRollDelayInSeconds, 0.1f))
        {
            LazyLoot.Config.MinRollDelayInSeconds = Math.Max(LazyLoot.Config.MinRollDelayInSeconds, 0.5f);

            LazyLoot.Config.MaxRollDelayInSeconds = Math.Max(LazyLoot.Config.MaxRollDelayInSeconds,
                LazyLoot.Config.MinRollDelayInSeconds + 0.1f);
        }
    }

    private static void DrawUserRestrictionEverywhere()
    {
        ImGui.Text("此頁設定會套用至所有物品，不論是否可交易。");
        ImGui.Separator();
        ImGui.Checkbox("放棄低於此品級的物品",
            ref LazyLoot.Config.RestrictionIgnoreItemLevelBelow);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(50);
        ImGui.DragInt("###RestrictionIgnoreItemLevelBelowValue",
            ref LazyLoot.Config.RestrictionIgnoreItemLevelBelowValue);
        if (LazyLoot.Config.RestrictionIgnoreItemLevelBelowValue < 0)
            LazyLoot.Config.RestrictionIgnoreItemLevelBelowValue = 0;

        ImGui.Checkbox(
            "放棄所有已解鎖物品（九宮幻卡、樂譜、褪色的樂譜、寵物、坐騎、情感動作、髮型）",
            ref LazyLoot.Config.RestrictionIgnoreItemUnlocked);

        if (!LazyLoot.Config.RestrictionIgnoreItemUnlocked)
        {
            ImGui.Checkbox("放棄已解鎖的坐騎", ref LazyLoot.Config.RestrictionIgnoreMounts);
            ImGui.Checkbox("放棄已解鎖的寵物", ref LazyLoot.Config.RestrictionIgnoreMinions);
            ImGui.Checkbox("放棄已解鎖的陸行鳥裝甲", ref LazyLoot.Config.RestrictionIgnoreBardings);
            ImGui.Checkbox("放棄已解鎖的九宮幻卡",
                ref LazyLoot.Config.RestrictionIgnoreTripleTriadCards);
            ImGui.Checkbox("放棄已解鎖的情感動作與髮型",
                ref LazyLoot.Config.RestrictionIgnoreEmoteHairstyle);
            ImGui.Checkbox("放棄已解鎖的樂譜",
                ref LazyLoot.Config.RestrictionIgnoreOrchestrionRolls);
            ImGui.Checkbox("放棄已解鎖的褪色的樂譜", ref LazyLoot.Config.RestrictionIgnoreFadedCopy);
        }

        ImGui.Checkbox("放棄目前職業無法使用的物品",
            ref LazyLoot.Config.RestrictionOtherJobItems);

        ImGui.Checkbox("不要擲骰有每週取得限制的物品",
            ref LazyLoot.Config.RestrictionWeeklyLockoutItems);

        ImGui.Checkbox("###RestrictionWeeklyLockoutItems", ref LazyLoot.Config.RestrictionLootLowerThanJobIlvl);
        ImGui.SameLine();
        ImGui.Text("選擇");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(80);
        ImGui.Combo("###RestrictionLootLowerThanJobIlvlRollState",
            ref LazyLoot.Config.RestrictionLootLowerThanJobIlvlRollState, new[] { "貪婪", "放棄" }, 2);
        ImGui.SameLine();
        ImGui.Text("於低於目前職業");
        ImGui.SetNextItemWidth(50);
        ImGui.SameLine();
        ImGui.DragInt("###RestrictionLootLowerThanJobIlvlTreshold",
            ref LazyLoot.Config.RestrictionLootLowerThanJobIlvlTreshold);
        if (LazyLoot.Config.RestrictionLootLowerThanJobIlvlTreshold < 0)
            LazyLoot.Config.RestrictionLootLowerThanJobIlvlTreshold = 0;
        ImGui.SameLine();
        ImGui.Text($"品級（★ {Utils.GetPlayerIlevel()}）指定差值的物品。");
        ImGuiComponents.HelpMarker("此設定僅套用於你可選擇需求的裝備。");

        ImGui.Checkbox("###RestrictionLootIsJobUpgrade", ref LazyLoot.Config.RestrictionLootIsJobUpgrade);
        ImGui.SameLine();
        ImGui.Text("選擇");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(80);
        ImGui.Combo("###RestrictionLootIsJobUpgradeRollState",
            ref LazyLoot.Config.RestrictionLootIsJobUpgradeRollState,
            new[] { "貪婪", "放棄" }, 2);
        ImGui.SameLine();
        ImGui.Text("於同部位已裝備物品品級較高時。");
        ImGuiComponents.HelpMarker("此設定僅套用於你可選擇需求的裝備。");

        ImGui.Checkbox("###RestrictionSeals", ref LazyLoot.Config.RestrictionSeals);
        ImGui.SameLine();
        ImGui.Text("放棄稀有品籌備軍票價值低於");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(100);
        ImGui.DragInt("###RestrictionSealsAmnt", ref LazyLoot.Config.RestrictionSealsAmnt);
        ImGui.SameLine();
        ImGui.Text($"（品級 {Roller.ConvertSealsToIlvl(LazyLoot.Config.RestrictionSealsAmnt)} 以下）");
        ImGuiComponents.HelpMarker(
            "此設定僅套用於可作為稀有品籌備繳交的裝備。");
    }

    private static void CenterText()
    {
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (ImGui.GetColumnWidth() - ImGui.GetFrameHeight()) * 0.5f);
    }

    private static ImTextureID GetDutyIcon(ContentFinderCondition duty)
    {
        var icon = duty is { HighEndDuty: true, ContentType.Value.RowId: 5 }
            ? Svc.Data.GetExcelSheet<ContentType>()
                .FirstOrDefault(x => x.RowId == 28).Icon
            : duty.ContentType.Value.Icon;
        if (icon == 0)
        {
            return 0;
        }

        var itemIcon = GetItemIcon(icon);
        return itemIcon?.Handle ?? default;
    }

    private static void DrawUserRestrictionItems()
    {
        ImGuiEx.LineCentered("ItemRestrictionWarning",
            () => ImGui.TextColored(ImGuiColors.DalamudYellow, "這些規則會覆寫其他所有限制設定"));
        ImGui.Separator();

        if (ImGui.BeginTable("UserRestrictionItemsTable", 8, ImGuiTableFlags.Borders))
        {
            ImGui.TableSetupColumn("啟用", ImGuiTableColumnFlags.WidthFixed, 50f);
            ImGui.TableSetupColumn("圖示", ImGuiTableColumnFlags.WidthFixed, 32f);
            ImGui.TableSetupColumn("名稱", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("需求", ImGuiTableColumnFlags.WidthFixed, 50f);
            ImGui.TableSetupColumn("貪婪", ImGuiTableColumnFlags.WidthFixed, 50f);
            ImGui.TableSetupColumn("放棄", ImGuiTableColumnFlags.WidthFixed, 50f);
            ImGui.TableSetupColumn("不處理", ImGuiTableColumnFlags.WidthFixed, 50f);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 60f);
            ImGui.TableHeadersRow();

            for (var i = 0; i < LazyLoot.Config.Restrictions.Items.Count; i++)
            {
                var item = LazyLoot.Config.Restrictions.Items[i];
                var restrictedItem = Svc.Data.GetExcelSheet<Item>().GetRow(item.Id);
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                var enabled = item.Enabled;
                CenterText();
                if (ImGui.Checkbox($"##{item.Id}", ref enabled))
                {
                    item.Enabled = enabled;
                    LazyLoot.Config.Save();
                }

                ImGui.TableNextColumn();
                CenterText();

                var icon = GetItemIcon(restrictedItem.Icon);
                if (icon != null)
                    ImGui.Image(icon.Handle, new Vector2(24, 24));
                else
                    ImGui.Text("-");

                ImGui.TableNextColumn();
                ImGui.Text(restrictedItem.Name.ToString());
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(restrictedItem.Name.ToString());

                ImGui.TableNextColumn();
                CenterText();
                if (ImGui.RadioButton($"##need{item.Id}", item.RollRule == RollResult.Needed))
                {
                    item.RollRule = RollResult.Needed;
                    LazyLoot.Config.Save();
                }

                ImGui.TableNextColumn();
                CenterText();
                if (ImGui.RadioButton($"##greed{item.Id}", item.RollRule == RollResult.Greeded))
                {
                    item.RollRule = RollResult.Greeded;
                    LazyLoot.Config.Save();
                }

                ImGui.TableNextColumn();
                CenterText();
                if (ImGui.RadioButton($"##pass{item.Id}", item.RollRule == RollResult.Passed))
                {
                    item.RollRule = RollResult.Passed;
                    LazyLoot.Config.Save();
                }

                ImGui.TableNextColumn();
                CenterText();
                if (ImGui.RadioButton($"##doNothing{item.Id}", item.RollRule == RollResult.UnAwarded))
                {
                    item.RollRule = RollResult.UnAwarded;
                    LazyLoot.Config.Save();
                }

                ImGui.TableNextColumn();
                if (ImGui.Button($"移除##{item.Id}"))
                {
                    LazyLoot.Config.Restrictions.Items.RemoveAt(i);
                    LazyLoot.Config.Save();
                    break;
                }
            }

            ImGui.EndTable();
        }

        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(2, 0));
        if (ImGui.Button("匯出", new Vector2(60, 0)))
        {
            var json = System.Text.Json.JsonSerializer.Serialize(LazyLoot.Config.Restrictions.Items);
            ImGui.SetClipboardText(json);
            Notify.Success("物品限制設定已複製到剪貼簿！");
        }

        ImGui.SameLine();
        if (ImGui.Button("匯入", new Vector2(60, 0)))
        {
            try
            {
                bool userImported = ImportFromClipboard("import_item_confirmation");
                if (!userImported)
                {
                    Notify.Error("無法匯入物品限制設定：格式無效");
                }
            }
            catch (Exception e)
            {
                e.Log();
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("新增物品", new Vector2(-1, 0)))
        {
            searchResultsQuery = "";
            ImGui.OpenPopup("item_search_add");
        }

        ImGui.PopStyleVar();

        var itemSheet = Svc.Data.GetExcelSheet<Item>();

        if (ImGui.BeginPopup("import_item_confirmation", ImGuiWindowFlags.AlwaysAutoResize))
        {
            bool validation = ValidateImport(itemSheet);
            if (!validation)
            {
                ImGui.CloseCurrentPopup();
            }
            ImGui.Text("確定要取代目前的物品限制設定嗎？");
            ImGuiEx.LineCentered(() => ImGuiEx.TextUnderlined("此操作無法復原。"));
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(40 / 255f, 167 / 255f, 69 / 255f, 1.0f));
            if (ImGui.Button("確定", new Vector2(100f, 0)))
            {
                if (importedRestrictions != null)
                {
                    LazyLoot.Config.Restrictions.Items = importedRestrictions;
                    LazyLoot.Config.Save();
                    Notify.Success("已成功匯入物品限制！");
                }

                ImGui.CloseCurrentPopup();
            }

            ImGui.PopStyleColor();
            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(220 / 255f, 53 / 255f, 69 / 255f, 1.0f));
            if (ImGui.Button("取消", new Vector2(-1, 0)))
            {
                ImGui.CloseCurrentPopup();
            }

            ImGui.PopStyleColor();
            ImGui.EndPopup();
        }

        if (ImGui.BeginPopup("item_search_add"))
        {
            ImGui.Text("搜尋物品：");
            var currentTime = ImGui.GetTime();
            if (ImGui.GetTime() > lastSearchTime + 0.1f)
            {
                lastSearchTime = currentTime;
                itemSearchResults = !string.IsNullOrEmpty(searchResultsQuery)
                    ? uint.TryParse(searchResultsQuery, out var searchId)
                        ? itemSheet.Where(x =>
                                x.RowId == searchId &&
                                LazyLoot.Config.Restrictions.Items.All(i => i.Id != x.RowId))
                            .Take(20)
                            .ToArray()
                        : itemSheet.Where(x =>
                                x.Name.ToString().Contains(searchResultsQuery, StringComparison.OrdinalIgnoreCase) &&
                                LazyLoot.Config.Restrictions.Items.All(i => i.Id != x.RowId))
                            .Take(20)
                            .ToArray()
                    : [];
            }

            var maxWidth = Math.Max(300f, itemSearchResults.Select(item =>
                ImGui.CalcTextSize($"{item.Name} (ID: {item.RowId})").X).DefaultIfEmpty(200f).Max() + 30);
            ImGui.SetNextItemWidth(maxWidth);
            ImGui.InputText("##itemSearch", ref searchResultsQuery, 100);
            if (!string.IsNullOrEmpty(searchResultsQuery))
                if (ImGui.BeginChild("itemSearchResults", new Vector2(maxWidth, 200), true))
                {
                    foreach (var item in itemSearchResults)
                    {
                        var icon = GetItemIcon(item.Icon);
                        if (icon != null)
                        {
                            ImGui.Image(icon.Handle, new Vector2(16, 16));
                            ImGui.SameLine();
                        }

                        if (!ImGui.Selectable($" {item.Name} (ID: {item.RowId})")) continue;
                        LazyLoot.Config.Restrictions.Items.Add(new CustomRestriction
                        {
                            Id = itemSheet.GetRow(item.RowId).RowId,
                            Enabled = true,
                            RollRule = RollResult.UnAwarded
                        });
                        LazyLoot.Config.Save();
                        ImGui.CloseCurrentPopup();
                    }

                    ImGui.EndChild();
                }

            ImGui.EndPopup();
        }
    }

    private static bool ValidateImport<T>(ExcelSheet<T> sheet) where T : struct, IExcelRow<T>
    {
        bool bail = false;
        foreach (var item in importedRestrictions)
        {
            if (sheet.Any(x => x.RowId == item.Id)) continue;
            bail = true;
            Notify.Error($"匯入的限制包含無效物品 ID：{item.Id}。已取消匯入。");
        }
        if (bail)
        {
            ImGui.CloseCurrentPopup();
            return false;
        }

        return true;
    }

    private static bool ImportFromClipboard(string popupname)
    {
        var clipboardText = ImGui.GetClipboardText();
        if (string.IsNullOrEmpty(clipboardText))
        {
            Notify.Error("剪貼簿中沒有可匯入的內容");
            return false;
        }
        try
        {
            var result = JsonSerializer.Deserialize<List<CustomRestriction>>(clipboardText);
            if (result != null)
            {
                importedRestrictions = result;
                ImGui.OpenPopup(popupname);
            }
        }
        catch (Exception e)
        {
            e.Log();
            return false;
        }

        return true;
    }

    private static void DrawUserRestrictionDuties()
    {
        ImGuiEx.LineCentered("ItemRestrictionWarning",
            () =>
            {
                var width = ImGui.GetWindowWidth() - 30;
                ImGui.PushTextWrapPos(width);
                ImGui.TextColored(ImGuiColors.DalamudYellow,
                    "這些規則會覆寫主要限制設定；若與物品限制衝突，則以物品限制為準。");
                ImGui.PopTextWrapPos();
            });
        ImGui.Separator();

        if (ImGui.BeginTable("UserRestrictionDutiesTable", 8, ImGuiTableFlags.Borders))
        {
            ImGui.TableSetupColumn("啟用", ImGuiTableColumnFlags.WidthFixed, 50f);
            ImGui.TableSetupColumn("類型", ImGuiTableColumnFlags.WidthFixed, 32f);
            ImGui.TableSetupColumn("名稱", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("需求", ImGuiTableColumnFlags.WidthFixed, 50f);
            ImGui.TableSetupColumn("貪婪", ImGuiTableColumnFlags.WidthFixed, 50f);
            ImGui.TableSetupColumn("放棄", ImGuiTableColumnFlags.WidthFixed, 50f);
            ImGui.TableSetupColumn("不處理", ImGuiTableColumnFlags.WidthFixed, 50f);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 60f);
            ImGui.TableHeadersRow();

            for (var i = 0; i < LazyLoot.Config.Restrictions.Duties.Count; i++)
            {
                var duty = LazyLoot.Config.Restrictions.Duties[i];
                var restrictedDuty = Svc.Data.GetExcelSheet<ContentFinderCondition>().GetRow(duty.Id);
                var enabled = duty.Enabled;
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                CenterText();
                if (ImGui.Checkbox($"##{duty.Id}", ref enabled))
                {
                    duty.Enabled = enabled;
                    LazyLoot.Config.Save();
                }

                ImGui.TableNextColumn();
                CenterText();

                ImGui.Image(GetDutyIcon(restrictedDuty), new Vector2(24, 24));
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip((restrictedDuty is { HighEndDuty: true, ContentType.Value.RowId: 5 }
                        ? Svc.Data.GetExcelSheet<ContentType>()
                            .FirstOrDefault(x => x.RowId == 28).Name
                        : restrictedDuty.ContentType.Value.Name).ToString());

                ImGui.TableNextColumn();
                ImGui.Text(restrictedDuty.Name.ToString());
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(restrictedDuty.Name.ToString());

                ImGui.TableNextColumn();
                CenterText();
                if (ImGui.RadioButton($"##need{duty.Id}", duty.RollRule == RollResult.Needed))
                {
                    duty.RollRule = RollResult.Needed;
                    LazyLoot.Config.Save();
                }

                ImGui.TableNextColumn();
                CenterText();
                if (ImGui.RadioButton($"##greed{duty.Id}", duty.RollRule == RollResult.Greeded))
                {
                    duty.RollRule = RollResult.Greeded;
                    LazyLoot.Config.Save();
                }

                ImGui.TableNextColumn();
                CenterText();
                if (ImGui.RadioButton($"##pass{duty.Id}", duty.RollRule == RollResult.Passed))
                {
                    duty.RollRule = RollResult.Passed;
                    LazyLoot.Config.Save();
                }

                ImGui.TableNextColumn();
                CenterText();
                if (ImGui.RadioButton($"##doNothing{duty.Id}", duty.RollRule == RollResult.UnAwarded))
                {
                    duty.RollRule = RollResult.UnAwarded;
                    LazyLoot.Config.Save();
                }

                ImGui.TableNextColumn();
                if (ImGui.Button($"移除##{duty.Id}"))
                {
                    LazyLoot.Config.Restrictions.Duties.RemoveAt(i);
                    LazyLoot.Config.Save();
                    break;
                }
            }

            ImGui.EndTable();
        }

        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(2, 0));
        if (ImGui.Button("匯出", new Vector2(60, 0)))
        {
            var json = System.Text.Json.JsonSerializer.Serialize(LazyLoot.Config.Restrictions.Duties);
            ImGui.SetClipboardText(json);
            Notify.Success("任務限制已複製到剪貼簿！");
        }

        ImGui.SameLine();
        if (ImGui.Button("匯入", new Vector2(60, 0)))
        {
            try
            {
                bool userImported = ImportFromClipboard("import_duty_confirmation");
                if (!userImported)
                {
                    Notify.Error("無法匯入任務限制設定：格式無效");
                }
            }
            catch (Exception e)
            {
                e.Log();
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("新增任務", new Vector2(-1, 0)))
        {
            searchResultsQuery = "";
            ImGui.OpenPopup("duty_search_add");
        }

        ImGui.PopStyleVar();

        var dutySheet = Svc.Data.GetExcelSheet<ContentFinderCondition>();

        if (ImGui.BeginPopup("import_duty_confirmation", ImGuiWindowFlags.AlwaysAutoResize))
        {
            bool validation = ValidateImport(dutySheet);
            if (!validation)
            {
                ImGui.CloseCurrentPopup();
            }
            ImGui.Text("確定要取代目前的任務限制設定嗎？");
            ImGuiEx.LineCentered(() => ImGuiEx.TextUnderlined("此操作無法復原。"));
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(40 / 255f, 167 / 255f, 69 / 255f, 1.0f));
            if (ImGui.Button("確定", new Vector2(100f, 0)))
            {
                if (importedRestrictions != null)
                {
                    LazyLoot.Config.Restrictions.Duties = importedRestrictions;
                    LazyLoot.Config.Save();
                    Notify.Success("已成功匯入任務限制！");
                }

                ImGui.CloseCurrentPopup();
            }

            ImGui.PopStyleColor();
            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(220 / 255f, 53 / 255f, 69 / 255f, 1.0f));
            if (ImGui.Button("取消", new Vector2(-1, 0)))
            {
                ImGui.CloseCurrentPopup();
            }

            ImGui.PopStyleColor();
            ImGui.EndPopup();
        }


        if (ImGui.BeginPopup("duty_search_add"))
        {
            ImGui.Text("搜尋任務：");
            var currentTime = ImGui.GetTime();
            if (ImGui.GetTime() > lastSearchTime + 0.1f)
            {
                lastSearchTime = currentTime;
                dutySearchResults = !string.IsNullOrEmpty(searchResultsQuery)
                    ? uint.TryParse(searchResultsQuery, out var searchId)
                        ? dutySheet.Where(x =>
                                x.RowId == searchId &&
                                LazyLoot.Config.Restrictions.Items.All(i => i.Id != x.RowId))
                            .Take(20)
                            .ToArray()
                        : dutySheet.Where(x =>
                                x.Name.ToString().Contains(searchResultsQuery,
                                    StringComparison.OrdinalIgnoreCase) &&
                                LazyLoot.Config.Restrictions.Items.All(i => i.Id != x.RowId))
                            .Take(20)
                            .ToArray()
                    : [];
            }

            var maxWidth = Math.Max(300f, dutySearchResults.Select(duty =>
                    ImGui.CalcTextSize($"{duty.Name} (ID: {duty.RowId})").X).DefaultIfEmpty(200f)
                .Max() + 30);
            ImGui.SetNextItemWidth(maxWidth);
            ImGui.InputText("##dutySearch", ref searchResultsQuery, 100);
            if (!string.IsNullOrEmpty(searchResultsQuery))
                if (ImGui.BeginChild("dutySearchResults", new Vector2(maxWidth, 200), true))
                {
                    foreach (var duty in dutySearchResults)
                    {
                        ImGui.Image(GetDutyIcon(duty), new Vector2(16, 16));
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip("任務類型");
                        ImGui.SameLine();
                        if (!ImGui.Selectable($" {duty.Name} (ID: {duty.RowId})")) continue;
                        LazyLoot.Config.Restrictions.Duties.Add(new CustomRestriction
                        {
                            Id = dutySheet.GetRow(duty.RowId).RowId,
                            Enabled = true,
                            RollRule = RollResult.UnAwarded
                        });
                        LazyLoot.Config.Save();
                        ImGui.CloseCurrentPopup();
                    }

                    ImGui.EndChild();
                }

            ImGui.EndPopup();
        }
    }

    private static void DrawUserRestriction()
    {
        if (ImGui.BeginTabBar("PerItemDutyConfigTabs"))
        {
            if (ImGui.BeginTabItem("一般規則"))
            {
                DrawUserRestrictionEverywhere();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("指定物品"))
            {
                DrawUserRestrictionItems();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("指定任務"))
            {
                DrawUserRestrictionDuties();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    private void DrawChatAndToast()
    {
        ImGuiEx.LineCentered("ChatInfoLabel", () => ImGuiEx.TextUnderlined("擲骰結果資訊"));
        ImGui.Checkbox("在聊天視窗顯示擲骰資訊", ref LazyLoot.Config.EnableChatLogMessage);
        ImGui.Spacing();
        ImGuiEx.LineCentered("ToastLabel", () => ImGuiEx.TextUnderlined("顯示為快顯訊息"));
        ImGuiComponents.HelpMarker("使用下列樣式將擲骰資訊顯示為快顯訊息。");
        ImGui.Checkbox("任務", ref LazyLoot.Config.EnableQuestToast);
        ImGui.SameLine();
        ImGui.Checkbox("一般", ref LazyLoot.Config.EnableNormalToast);
        ImGui.SameLine();
        ImGui.Checkbox("錯誤", ref LazyLoot.Config.EnableErrorToast);
    }

    private void DrawFulf()
    {
        ImGuiEx.LineCentered("FULFLabel", () => ImGuiEx.TextUnderlined("終極懶人功能（FULF）"));

        ImGui.TextWrapped(
            "終極懶人功能（FULF）可依設定自動為物品擲骰，不必每次使用上方指令。");
        ImGui.Separator();
        ImGui.Columns(2, ImU8String.Empty, false);
        ImGui.SetColumnWidth(0, 80);
        ImGui.Text("/fulf need");
        ImGui.NextColumn();
        ImGui.Text("將 FULF 設為需求模式，遵循 /lazy need 規則。");
        ImGui.NextColumn();
        ImGui.Text("/fulf greed");
        ImGui.NextColumn();
        ImGui.Text("將 FULF 設為貪婪模式，遵循 /lazy greed 規則。");
        ImGui.NextColumn();
        ImGui.Text("/fulf pass");
        ImGui.NextColumn();
        ImGui.Text("將 FULF 設為放棄模式，遵循 /lazy pass 規則。");
        ImGui.NextColumn();
        ImGui.Columns(1);
        ImGui.Separator();
        ImGui.Checkbox("###FulfEnabled", ref LazyLoot.Config.FulfEnabled);
        ImGui.SameLine();
        ImGui.TextColored(LazyLoot.Config.FulfEnabled ? ImGuiColors.HealerGreen : ImGuiColors.DalamudRed,
            LazyLoot.Config.FulfEnabled ? "FULF 已啟用" : "FULF 已停用");

        ImGui.SetNextItemWidth(100);

        if (ImGui.Combo("擲骰選項", ref LazyLoot.Config.FulfRoll, new[] { "需求", "貪婪", "放棄" }, 3))
            LazyLoot.Config.Save();

        ImGui.Text("首次擲骰延遲範圍（秒）");
        ImGui.SetNextItemWidth(100);
        ImGui.DragFloat("最短延遲（秒）", ref LazyLoot.Config.FulfMinRollDelayInSeconds, 0.1F);

        if (LazyLoot.Config.FulfMinRollDelayInSeconds >= LazyLoot.Config.FulfMaxRollDelayInSeconds)
            LazyLoot.Config.FulfMinRollDelayInSeconds = LazyLoot.Config.FulfMaxRollDelayInSeconds - 0.1f;

        if (LazyLoot.Config.FulfMinRollDelayInSeconds < 1.5f) LazyLoot.Config.FulfMinRollDelayInSeconds = 1.5f;

        ImGui.SetNextItemWidth(100);
        ImGui.DragFloat("最長延遲（秒）", ref LazyLoot.Config.FulfMaxRollDelayInSeconds, 0.1F);

        if (LazyLoot.Config.FulfMaxRollDelayInSeconds <= LazyLoot.Config.FulfMinRollDelayInSeconds)
            LazyLoot.Config.FulfMaxRollDelayInSeconds = LazyLoot.Config.FulfMinRollDelayInSeconds + 0.1f;
    }
}
