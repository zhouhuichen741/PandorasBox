using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Hooking;
using Dalamud.Memory;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.Exd;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using PandorasBox.FeaturesSetup;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PandorasBox.Features.UI
{
    internal class TrackOutfits : Feature
    {
        public override string Name => "套装幻影化收集助手";

        public override string Description => "在物品提示框中显示你是否已拥有该物品所属的套装";

        public override FeatureType FeatureType => FeatureType.UI;

        private record OwnedOutfit(uint SetId, List<uint> OwnedItems, List<uint> MissingItems);

        private DateTime ownedOutfitCacheLastUpdate = DateTime.MinValue;
        private List<OwnedOutfit> ownedOutfits = new();
        
        private Hook<GenerateItemTooltipDelegate>? generateItemTooltipHook;
        private Hook<UIState.Delegates.IsItemActionUnlocked>? isItemActionUnlockedHook;
        
        private const int TooltipItemDescriptionField = 13;
        private const string OutfitOwnedStatusText = "已收录";
        private const string OutfitMissingStatusText = "未收录";
        private const string OutfitSectionTitle = "套装幻影化";

        private unsafe delegate void* GenerateItemTooltipDelegate(AtkUnitBase* addonItemDetail, NumberArrayData* numberArrayData, StringArrayData* stringArrayData);

        public override unsafe void Enable()
        {
            Svc.AddonLifecycle.RegisterListener(AddonEvent.PreRequestedUpdate, "ItemDetail", OnItemDetailRequestedUpdate);
            Svc.AddonLifecycle.RegisterListener(AddonEvent.PreRequestedUpdate, "ItemDetailCompare", OnItemDetailRequestedUpdate);

            generateItemTooltipHook ??= Svc.Hook.HookFromSignature<GenerateItemTooltipDelegate>(
                "48 89 5c 24 ?? 55 56 57 41 54 41 55 41 56 41 57 48 83 ec ?? 48 8b 42 ?? 4c 8b ea",
                GenerateItemTooltipDetour);
            generateItemTooltipHook?.Enable();

            isItemActionUnlockedHook ??= Svc.Hook.HookFromAddress<UIState.Delegates.IsItemActionUnlocked>(
                (nint)UIState.MemberFunctionPointers.IsItemActionUnlocked,
                IsItemActionUnlockedDetour);
            isItemActionUnlockedHook?.Enable();

            base.Enable();
        }

        public override void Disable()
        {
            Svc.AddonLifecycle.UnregisterListener(AddonEvent.PreRequestedUpdate, "ItemDetail", OnItemDetailRequestedUpdate);
            Svc.AddonLifecycle.UnregisterListener(AddonEvent.PreRequestedUpdate, "ItemDetailCompare", OnItemDetailRequestedUpdate);
            generateItemTooltipHook?.Disable();
            isItemActionUnlockedHook?.Disable();
            base.Disable();
        }

        public override void Dispose()
        {
            generateItemTooltipHook?.Dispose();
            isItemActionUnlockedHook?.Dispose();
            generateItemTooltipHook = null;
            isItemActionUnlockedHook = null;
            base.Dispose();
        }

        private unsafe long IsItemActionUnlockedDetour(UIState* uiState, void* item)
        {
            var itemId = GetTooltipItemId();
            if (itemId == 0)
            {
                return isItemActionUnlockedHook!.Original(uiState, item);
            }

            var baseItemId = NormalizeItemId(itemId);
            var tooltipItemRow = ExdModule.GetItemRowById(baseItemId);
            if (tooltipItemRow == null || item != tooltipItemRow)
            {
                return isItemActionUnlockedHook!.Original(uiState, item);
            }

            var outfits = GetOutfits(baseItemId);

            if (outfits is { Length: > 0 })
            {
                var ownedOutfitsBySet = GetOwnedOutfits().ToDictionary(oo => oo.SetId);
                foreach (var outfit in outfits)
                {
                    ownedOutfitsBySet.TryGetValue(outfit, out var ownedOutfit);
                    var hasItem = ownedOutfit?.OwnedItems.Contains(baseItemId) ?? false;
                    if (!hasItem)
                    {
                        return 2;
                    }
                }

                return 1;
            }

            return isItemActionUnlockedHook!.Original(uiState, item);
        }

        private unsafe void* GenerateItemTooltipDetour(AtkUnitBase* addonItemDetail, NumberArrayData* numberArrayData, StringArrayData* stringArrayData)
        {
            var result = generateItemTooltipHook!.Original(addonItemDetail, numberArrayData, stringArrayData);

            var itemId = GetTooltipItemId();
            if (itemId != 0)
            {
                InjectTooltipText(stringArrayData, itemId);
            }

            return result;
        }

        private unsafe void OnItemDetailRequestedUpdate(AddonEvent type, AddonArgs args)
        {
            if (args is not AddonRequestedUpdateArgs requestedUpdateArgs)
            {
                return;
            }

            var addon = (AtkUnitBase*)args.Addon.Address;
            if (addon == null || !addon->IsVisible)
            {
                return;
            }

            var numberArrayData = ((NumberArrayData**)requestedUpdateArgs.NumberArrayData)[30];
            var stringArrayData = ((StringArrayData**)requestedUpdateArgs.StringArrayData)[27];
            if (numberArrayData == null || stringArrayData == null)
            {
                return;
            }

            var itemId = GetTooltipItemId();
            if (itemId == 0)
            {
                return;
            }

            InjectTooltipText(stringArrayData, itemId);
        }

        private unsafe uint GetTooltipItemId()
        {
            var agentItemDetail = AgentItemDetail.Instance();
            if (agentItemDetail != null && agentItemDetail->ItemId != 0)
            {
                return NormalizeItemId(agentItemDetail->ItemId);
            }

            var hoveredGameItem = Svc.GameGui.HoveredItem;
            if (hoveredGameItem > 0 && hoveredGameItem < 2_000_000)
            {
                return NormalizeItemId((uint)hoveredGameItem);
            }

            return 0;
        }

        private static uint NormalizeItemId(uint itemId)
        {
            if (itemId == 0) return 0;

            var normalizedItemId = itemId % 1_000_000;
            if (normalizedItemId >= 500_000)
            {
                normalizedItemId -= 500_000;
            }

            return normalizedItemId;
        }

        private unsafe List<OwnedOutfit> GetOwnedOutfits()
        {
            var cacheTime = AgentMiragePrismPrismBox.Instance()->IsAgentActive() ? 1 : 30;
            if (DateTime.Now - ownedOutfitCacheLastUpdate < TimeSpan.FromSeconds(cacheTime))
            {
                return ownedOutfits;
            }
            
            ownedOutfitCacheLastUpdate = DateTime.Now;
            var l = new List<OwnedOutfit>();
            var agent = ItemFinderModule.Instance();
            
            for (ushort i = 0; i < agent->GlamourDresserItemIds.Length && i < agent->GlamourDresserItemSetUnlockBits.Length; i++)
            {
                var itemId = agent->GlamourDresserItemIds[i];
                if (itemId == 0) continue;
                if (!Svc.Data.GetExcelSheet<MirageStoreSetItem>().TryGetRow(itemId, out var row)) continue;
                
                var ownedItems = new List<uint>();
                var missingItems = new List<uint>();
                var bits = ItemFinderModule.Instance()->GlamourDresserItemSetUnlockBits[i];
                
                for (int j = 0; j < 11; j++)
                {
                    uint? slotItemId = null;
                    switch (j)
                    {
                        case 0: if (row.MainHand.RowId != 0) slotItemId = row.MainHand.RowId; break;
                        case 1: if (row.OffHand.RowId != 0) slotItemId = row.OffHand.RowId; break;
                        case 2: if (row.Head.RowId != 0) slotItemId = row.Head.RowId; break;
                        case 3: if (row.Body.RowId != 0) slotItemId = row.Body.RowId; break;
                        case 4: if (row.Hands.RowId != 0) slotItemId = row.Hands.RowId; break;
                        case 5: if (row.Legs.RowId != 0) slotItemId = row.Legs.RowId; break;
                        case 6: if (row.Feet.RowId != 0) slotItemId = row.Feet.RowId; break;
                        case 7: if (row.Earrings.RowId != 0) slotItemId = row.Earrings.RowId; break;
                        case 8: if (row.Necklace.RowId != 0) slotItemId = row.Necklace.RowId; break;
                        case 9: if (row.Bracelets.RowId != 0) slotItemId = row.Bracelets.RowId; break;
                        case 10: if (row.Ring.RowId != 0) slotItemId = row.Ring.RowId; break;
                    }
                    
                    if (slotItemId == null || slotItemId.Value == 0) continue;
                    
                    var isUnlocked = MirageManager.Instance()->PrismBoxLoaded 
                        ? MirageManager.Instance()->IsSetSlotUnlocked(i, j) 
                        : (((bits >> j) & 1) == 0);
                    
                    if (isUnlocked)
                    {
                        ownedItems.Add(slotItemId.Value);
                    }
                    else
                    {
                        missingItems.Add(slotItemId.Value);
                    }
                }
                l.Add(new OwnedOutfit(itemId, ownedItems, missingItems));
            }
            
            ownedOutfits = l
                .GroupBy(x => x.SetId)
                .Select(group =>
                {
                    var ownedItems = group
                        .SelectMany(x => x.OwnedItems)
                        .Distinct()
                        .ToList();
                    var missingItems = group
                        .SelectMany(x => x.MissingItems)
                        .Where(x => !ownedItems.Contains(x))
                        .Distinct()
                        .ToList();

                    return new OwnedOutfit(group.Key, ownedItems, missingItems);
                })
                .ToList();
            
            return ownedOutfits;
        }

        private static uint[] GetOutfits(uint itemId)
        {
            return Svc.Data.GetExcelSheet<MirageStoreSetItemLookup>()
                .Where(row => row.RowId == itemId)
                .SelectMany(row => row.Item.Where(x => x.Value.RowId != 0))
                .Select(x => x.Value.RowId)
                .ToArray();
        }

        private unsafe void InjectTooltipText(StringArrayData* stringArrayData, uint itemId)
        {
            if (stringArrayData == null)
            {
                return;
            }

            var baseItemId = NormalizeItemId(itemId);
            var outfits = GetOutfits(baseItemId);
            var descriptionField = TooltipItemDescriptionField;
            var description = GetTooltipString(stringArrayData, descriptionField);

            if (description == null)
            {
                return;
            }

            if (description.TextValue.Contains(OutfitSectionTitle, StringComparison.Ordinal))
            {
                return;
            }

            if (outfits is not { Length: > 0 })
            {
                return;
            }

            var newDescription = CloneSeString(description);
            if (newDescription.Payloads.Count > 0)
            {
                newDescription.Payloads.Add(new NewLinePayload());
            }

            newDescription.Payloads.Add(new TextPayload(OutfitSectionTitle));

            var ownedOutfitsBySet = GetOwnedOutfits().ToDictionary(oo => oo.SetId);
            foreach (var outfit in outfits)
            {
                ownedOutfitsBySet.TryGetValue(outfit, out var ownedOutfit);
                var isOutfitOwned = ownedOutfit?.OwnedItems.Contains(baseItemId) ?? false;

                newDescription.Payloads.Add(new NewLinePayload());
                newDescription.Payloads.Add(new UIForegroundPayload((ushort)(isOutfitOwned ? 45 : 14)));
                var itemName = Svc.Data.GetExcelSheet<Item>().GetRow(outfit).Name;
                var statusText = isOutfitOwned ? OutfitOwnedStatusText : OutfitMissingStatusText;
                newDescription.Payloads.Add(new TextPayload($"    {itemName} ({statusText})"));
                newDescription.Payloads.Add(new UIForegroundPayload(0));
            }

            SetTooltipString(stringArrayData, descriptionField, newDescription);
        }

        private static SeString CloneSeString(SeString source)
        {
            var clone = new SeString();
            foreach (var payload in source.Payloads)
            {
                clone.Payloads.Add(payload);
            }

            return clone;
        }

        private static unsafe SeString? GetTooltipString(StringArrayData* stringArrayData, int field)
        {
            try
            {
                if (stringArrayData == null || stringArrayData->AtkArrayData.Size <= field)
                {
                    return null;
                }

                var stringAddress = new nint(stringArrayData->StringArray[field]);
                return stringAddress == nint.Zero ? null : MemoryHelper.ReadSeStringNullTerminated(stringAddress);
            }
            catch
            {
                return new SeString();
            }
        }

        private static unsafe void SetTooltipString(StringArrayData* stringArrayData, int field, SeString value)
        {
            value ??= new SeString();
            var bytes = value.Encode().ToList();
            bytes.Add(0);
            var encoded = bytes.ToArray();

            fixed (byte* encodedPtr = encoded)
            {
                stringArrayData->SetValue(field, encodedPtr, false, true, true);
            }
        }
    }
}
