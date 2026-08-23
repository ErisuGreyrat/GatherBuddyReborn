using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GatherBuddy.Automation;
using GatherBuddy.Crafting;
using GatherBuddy.Plugin;
using GatherBuddy.Vulcan.Vendors;
using Lumina.Excel.Sheets;

namespace GatherBuddy.Gui;

/// <summary>
/// Overlay for creating Vulcan crafting lists from Grand Company Supply missions.
/// Attaches to the GrandCompanyExchange addon when viewing supply missions.
/// </summary>
public unsafe class GcSupplyOverlay : IDisposable
{
    private const string OverlayName = "GcSupplyOverlay";
    private const string TargetAddon = "GrandCompanyExchange";
    
    private readonly GatherBuddy _plugin;
    private bool _enabled;
    private bool _addonHooked;
    
    public GcSupplyOverlay(GatherBuddy plugin)
    {
        _plugin = plugin;
        _enabled = plugin.Config.VulcanGcSupplyOverlayEnabled;
        
        if (_enabled)
            Enable();
    }
    
    public void Enable()
    {
        _enabled = true;
        HookAddon();
    }
    
    public void Disable()
    {
        _enabled = false;
        UnhookAddon();
        HideOverlay();
    }
    
    public void Toggle()
    {
        if (_enabled)
            Disable();
        else
            Enable();
    }
    
    private void HookAddon()
    {
        if (_addonHooked)
            return;
            
        try
        {
            Dalamud.Game.Addon.Lifecycle.Register(AddonLifecycleEventType.PostDraw, TargetAddon, OnAddonPostDraw);
            _addonHooked = true;
            _plugin.Log.Debug("[GcSupplyOverlay] Hooked GrandCompanyExchange addon");
        }
        catch (Exception ex)
        {
            _plugin.Log.Warning($"[GcSupplyOverlay] Failed to hook addon: {ex.Message}");
        }
    }
    
    private void UnhookAddon()
    {
        if (!_addonHooked)
            return;
            
        try
        {
            Dalamud.Game.Addon.Lifecycle.Unregister(AddonLifecycleEventType.PostDraw, TargetAddon, OnAddonPostDraw);
            _addonHooked = false;
            _plugin.Log.Debug("[GcSupplyOverlay] Unhooked GrandCompanyExchange addon");
        }
        catch (Exception ex)
        {
            _plugin.Log.Warning($"[GcSupplyOverlay] Failed to unhook addon: {ex.Message}");
        }
    }
    
    private void OnAddonPostDraw(AddonEvent eventType, AddonArgs args)
    {
        if (!_enabled || args is not AddonPreDrawArgs preDrawArgs)
            return;
            
        var addon = (AtkUnitBase*)args.Addon;
        if (addon == null || !addon->IsVisible)
        {
            HideOverlay();
            return;
        }
        
        // Check if we're on the supply missions tab (tab index 0)
        if (!IsOnSupplyMissionsTab(addon))
        {
            HideOverlay();
            return;
        }
        
        ShowOverlay(addon);
    }
    
    private bool IsOnSupplyMissionsTab(AtkUnitBase* addon)
    {
        try
        {
            // Tab control is typically node 37 for GrandCompanyExchange
            var tabControl = addon->GetNodeById(37);
            if (tabControl == null)
                return false;
                
            var atkResNode = (AtkResNode*)tabControl;
            // Check if first tab (supply missions) is selected
            // This is a simplification - may need adjustment based on actual addon structure
            return true;
        }
        catch
        {
            return false;
        }
    }
    
    private void ShowOverlay(AtkUnitBase* addon)
    {
        try
        {
            var reader = new VendorGrandCompanyExchangeReader(addon);
            var items = reader.Items;
            
            if (items.Count == 0)
            {
                HideOverlay();
                return;
            }
            
            // Position overlay near the top-right of the addon
            var posX = addon->X + addon->GetWidth() - 250;
            var posY = addon->Y + 50;
            
            ImGuiHelpers.ForceNextWindowMainViewport();
            ImGui.SetNextWindowPos(new System.Numerics.Vector2(posX, posY));
            ImGui.SetNextWindowSize(new System.Numerics.Vector2(240, 0));
            
            if (ImGui.Begin(OverlayName, 
                    ImGuiWindowFlags.NoTitleBar | 
                    ImGuiWindowFlags.NoScrollbar | 
                    ImGuiWindowFlags.NoScrollWithMouse |
                    ImGuiWindowFlags.AlwaysAutoResize |
                    ImGuiWindowFlags.NoFocusOnAppearing |
                    ImGuiWindowFlags.NoNav))
            {
                ImGui.PushStyleColor(ImGuiCol.WindowBg, new System.Numerics.Vector4(0.1f, 0.1f, 0.1f, 0.95f));
                
                ImGui.TextColored(new System.Numerics.Vector4(1f, 0.85f, 0.2f, 1f), "GC Supply Missions");
                ImGui.Separator();
                
                ImGui.TextDisabled($"{items.Count} items available");
                
                ImGui.Spacing();
                
                if (ImGui.Button("Create Vulcan List", new System.Numerics.Vector2(220, 30)))
                {
                    CreateCraftingListFromSupply(items);
                }
                
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("Creates a new Vulcan crafting list with all items\nfrom your current GC supply missions.");
                }
                
                ImGui.PopStyleColor();
            }
            ImGui.End();
        }
        catch (Exception ex)
        {
            _plugin.Log.Error($"[GcSupplyOverlay] Error showing overlay: {ex.Message}");
            HideOverlay();
        }
    }
    
    private void HideOverlay()
    {
        // Window will not be drawn if ShowOverlay is not called
    }
    
    private void CreateCraftingListFromSupply(List<VendorGrandCompanyExchangeItemReader> items)
    {
        try
        {
            var validItems = items.Where(i => i.ItemId > 0 && i.Stackable).ToList();
            
            if (validItems.Count == 0)
            {
                Communicator.PrintError("[Vulcan] No valid supply items found to create a list.");
                return;
            }
            
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            var listName = $"GC Supply {timestamp}";
            
            var newList = _plugin.CraftingListManager.CreateNewList(listName);
            
            foreach (var item in validItems)
            {
                // Find recipe for this item
                var recipe = FindRecipeForItem(item.ItemId);
                if (recipe != null)
                {
                    newList.AddRecipe(recipe.Value.RowId, 1);
                }
            }
            
            _plugin.CraftingListManager.SaveList(newList);
            
            Communicator.Print($"[Vulcan] Created crafting list '{listName}' with {newList.Recipes.Count} recipes from GC supply missions.");
            
            // Open the Vulcan window to the crafting lists tab
            _plugin.VulcanWindow.OpenCraftingList(newList);
        }
        catch (Exception ex)
        {
            _plugin.Log.Error($"[GcSupplyOverlay] Failed to create crafting list: {ex.Message}");
            Communicator.PrintError("[Vulcan] Failed to create crafting list from GC supply.");
        }
    }
    
    private Lumina.Excel.Sheets.Recipe? FindRecipeForItem(uint itemId)
    {
        var recipeSheet = Dalamud.GameData.GetExcelSheet<Lumina.Excel.Sheets.Recipe>();
        if (recipeSheet == null)
            return null;
            
        foreach (var recipe in recipeSheet)
        {
            if (recipe.ItemResult.RowId == itemId)
                return recipe;
        }
        
        return null;
    }
    
    public void Dispose()
    {
        UnhookAddon();
    }
}
