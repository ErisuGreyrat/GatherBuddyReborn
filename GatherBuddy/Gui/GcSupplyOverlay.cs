using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Interface.Windowing;
using ElliLib.Raii;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType;
using GatherBuddy.Crafting;
using Lumina.Excel.Sheets;

namespace GatherBuddy.Gui;

/// <summary>
/// Overlay on the Grand Company supply window that creates a normal Vulcan crafting list
/// from the current supply commitments (NeekoGc SupplyOverlay behaviour, in-tree).
/// </summary>
public sealed class GcSupplyOverlay : IDisposable
{
    private const string SupplyListAddon  = "GrandCompanySupplyList";
    private const string SupplyRewardAddon = "GrandCompanySupplyReward";

    private bool _registered;
    private bool _listOpen;
    private string _statusMessage = string.Empty;
    private Vector4 _statusColor = new(0.7f, 0.7f, 0.7f, 1f);
    private DateTime _statusUntil = DateTime.MinValue;

    public void Enable()
    {
        if (_registered)
            return;
        try
        {
            Dalamud.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, SupplyListAddon, OnSupplyList);
            Dalamud.AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, SupplyListAddon, OnSupplyList);
            Dalamud.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, SupplyListAddon, OnSupplyListFinalize);
            _registered = true;
            GatherBuddy.Log.Information("[GcSupplyOverlay] Registered GrandCompanySupplyList listeners.");
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Warning($"[GcSupplyOverlay] Failed to register: {ex.Message}");
        }
    }

    public void Disable()
    {
        if (!_registered)
            return;
        try
        {
            Dalamud.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, SupplyListAddon, OnSupplyList);
            Dalamud.AddonLifecycle.UnregisterListener(AddonEvent.PostRefresh, SupplyListAddon, OnSupplyList);
            Dalamud.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, SupplyListAddon, OnSupplyListFinalize);
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Warning($"[GcSupplyOverlay] Failed to unregister: {ex.Message}");
        }

        _registered = false;
        _listOpen = false;
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled) Enable();
        else Disable();
    }

    public void Dispose()
        => Disable();

    private void OnSupplyList(AddonEvent type, AddonArgs args)
        => _listOpen = true;

    private void OnSupplyListFinalize(AddonEvent type, AddonArgs args)
        => _listOpen = false;

    public void Draw()
    {
        if (!GatherBuddy.Config.EnableGcSupplyOverlay)
            return;
        if (!_listOpen)
            return;

        var addon = Dalamud.GameGui.GetAddonByName(SupplyListAddon, 1);
        if (addon == nint.Zero)
        {
            _listOpen = false;
            return;
        }

        unsafe
        {
            var unit = (AtkUnitBase*)(nint)addon;
            if (unit == null || !unit->IsVisible)
                return;

            var posX = unit->X;
            var posY = unit->Y;
            var width = unit->GetScaledWidth(true);
            ImGui.SetNextWindowPos(new Vector2(posX, Math.Max(0, posY - 36f)), ImGuiCond.Always);
            ImGui.SetNextWindowSize(new Vector2(Math.Max(280f, width), 0), ImGuiCond.Always);
        }

        const ImGuiWindowFlags flags =
            ImGuiWindowFlags.NoDecoration
            | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.AlwaysAutoResize
            | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoFocusOnAppearing
            | ImGuiWindowFlags.NoNav;

        if (!ImGui.Begin("###GcSupplyOverlayBar", flags))
        {
            ImGui.End();
            return;
        }

        using var theme = VulcanUiStyle.PushTheme();

        if (ImGui.Button("Create Vulcan list from supply"))
            TryCreateListFromSupply();

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Scan the open Grand Company supply list and create a normal Vulcan crafting list from craftable commitments.");

        if (DateTime.UtcNow < _statusUntil && !string.IsNullOrEmpty(_statusMessage))
        {
            ImGui.SameLine();
            ImGui.TextColored(_statusColor, _statusMessage);
        }

        ImGui.End();
    }

    private void SetStatus(string message, bool ok)
    {
        _statusMessage = message;
        _statusColor = ok
            ? new Vector4(0.45f, 1.00f, 0.45f, 1f)
            : new Vector4(1.00f, 0.55f, 0.45f, 1f);
        _statusUntil = DateTime.UtcNow.AddSeconds(8);
        if (ok)
            GatherBuddy.Log.Information($"[GcSupplyOverlay] {message}");
        else
            GatherBuddy.Log.Warning($"[GcSupplyOverlay] {message}");
    }

    private void TryCreateListFromSupply()
    {
        try
        {
            var commitments = ReadSupplyCommitments();
            if (commitments.Count == 0)
            {
                SetStatus("No craftable supply items found on the open list.", false);
                return;
            }

            var manager = GatherBuddy.CraftingListManager;
            if (manager == null)
            {
                SetStatus("Crafting list manager unavailable.", false);
                return;
            }

            var name = $"GC Supply {DateTime.Now:yyyy-MM-dd HH:mm}";
            var baseName = name;
            var suffix = 1;
            while (!manager.IsNameUnique(name))
            {
                suffix++;
                name = $"{baseName} ({suffix})";
            }

            var list = manager.CreateNewList(name);
            var added = 0;
            foreach (var (recipeId, qty) in commitments)
            {
                if (recipeId == 0 || qty <= 0)
                    continue;
                list.AddRecipe(recipeId, qty);
                added++;
            }

            if (added == 0)
            {
                manager.DeleteList(list.ID);
                SetStatus("No matching recipes for supply items.", false);
                return;
            }

            manager.SaveList(list);
            SetStatus($"Created list \"{list.Name}\" with {added} recipe(s).", true);

            try
            {
                GatherBuddy.VulcanWindow?.OpenCraftingListFromExternal(list);
            }
            catch
            {
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Failed: {ex.Message}", false);
        }
    }

    private unsafe List<(uint RecipeId, int Quantity)> ReadSupplyCommitments()
    {
        var results = new List<(uint RecipeId, int Quantity)>();
        var addonPtr = Dalamud.GameGui.GetAddonByName(SupplyListAddon, 1);
        if (addonPtr == nint.Zero)
            return results;

        var unit = (AtkUnitBase*)(nint)addonPtr;
        if (unit == null || !unit->IsVisible)
            return results;

        var itemSheet = Dalamud.GameData.GetExcelSheet<Item>();
        var recipeSheet = Dalamud.GameData.GetExcelSheet<Recipe>();
        if (itemSheet == null || recipeSheet == null)
            return results;

        var itemToRecipe = new Dictionary<uint, uint>();
        foreach (var recipe in recipeSheet)
        {
            var resultId = recipe.ItemResult.RowId;
            if (resultId == 0)
                continue;
            if (!itemToRecipe.ContainsKey(resultId))
                itemToRecipe[resultId] = recipe.RowId;
        }

        var foundItemIds = new HashSet<uint>();

        try
        {
            var valueCount = unit->AtkValuesCount;
            var values = unit->AtkValues;
            if (values != null && valueCount > 0)
            {
                for (var i = 0; i < valueCount; i++)
                {
                    var v = values[i];
                    uint candidate = 0;
                    if (v.Type == ValueType.UInt)
                        candidate = v.UInt;
                    else if (v.Type == ValueType.Int && v.Int > 0)
                        candidate = (uint)v.Int;
                    if (candidate == 0 || candidate > 200_000)
                        continue;
                    if (itemSheet.TryGetRow(candidate, out _) && itemToRecipe.ContainsKey(candidate))
                        foundItemIds.Add(candidate);
                }
            }
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Debug($"[GcSupplyOverlay] AtkValues scan failed: {ex.Message}");
        }

        if (foundItemIds.Count == 0)
        {
            try
            {
                CollectItemIdsFromAddon(unit, foundItemIds);
            }
            catch (Exception ex)
            {
                GatherBuddy.Log.Debug($"[GcSupplyOverlay] Addon walk failed: {ex.Message}");
            }
        }

        foreach (var itemId in foundItemIds)
        {
            if (!itemToRecipe.TryGetValue(itemId, out var recipeId))
                continue;
            results.Add((recipeId, 1));
        }

        return results;
    }

    private static unsafe void CollectItemIdsFromAddon(AtkUnitBase* unit, HashSet<uint> foundItemIds)
    {
        if (unit == null || unit->UldManager.NodeList == null)
            return;

        var count = unit->UldManager.NodeListCount;
        for (var i = 0; i < count; i++)
        {
            var node = unit->UldManager.NodeList[i];
            if (node == null)
                continue;
            WalkNode(node, foundItemIds);
        }
    }

    private static unsafe void WalkNode(AtkResNode* node, HashSet<uint> foundItemIds)
    {
        if (node == null)
            return;

        if (node->Type == NodeType.Component)
        {
            var component = ((AtkComponentNode*)node)->Component;
            if (component != null)
            {
            }
        }

        if ((int)node->ChildCount > 0 && node->ChildNode != null)
        {
            var child = node->ChildNode;
            for (var i = 0; i < node->ChildCount && child != null; i++)
            {
                WalkNode(child, foundItemIds);
                child = child->PrevSiblingNode;
            }
        }
    }
}
