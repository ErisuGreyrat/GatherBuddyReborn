using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using ElliLib.Raii;
using GatherBuddy.Classes;
using GatherBuddy.Enums;
using GatherBuddy.Interfaces;
using GatherBuddy.Plugin;
using GatheringType = GatherBuddy.Enums.GatheringType;

namespace GatherBuddy.Gui;

/// <summary>
/// Click gather/fish source icons on a Materials row → location popup with flag + teleport
/// (parity with mob-drop popup UX in CraftingMaterialsWindow).
/// </summary>
internal static class GatherSourcePopup
{
    private static readonly Vector4 MarkerButtonColor   = new(0.45f, 0.80f, 1.00f, 1f);
    private static readonly Vector4 TeleportButtonColor = new(0.60f, 0.95f, 0.60f, 1f);
    private static readonly Dictionary<string, bool> ZoneOpenStates = new(StringComparer.Ordinal);

    public static bool TryDrawGatherIconButton(uint itemId, uint iconId, string tooltip, float iconSize, bool addLeadingSpacing, float spacing)
    {
        if (!IsGatherSourceIcon(itemId, iconId))
            return false;

        var locations = ResolveLocations(itemId);
        if (locations.Count == 0)
            return false;

        if (addLeadingSpacing)
            ImGui.SameLine(0, spacing);

        var popupId = $"##gathersrc_{itemId}_{iconId}";
        var size = new Vector2(iconSize, iconSize);

        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, Vector2.Zero);
        ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(1f, 1f, 1f, 0.15f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(1f, 1f, 1f, 0.30f));
        var clicked = false;
        ImGui.PushID(popupId);
        var icon = Icons.DefaultStorage.TextureProvider.GetFromGameIcon(new GameIconLookup(iconId));
        if (icon.TryGetWrap(out var wrap, out _))
            clicked = ImGui.ImageButton(wrap.Handle, size);
        else
            ImGui.Dummy(size);
        ImGui.PopID();
        ImGui.PopStyleColor(3);
        ImGui.PopStyleVar();

        if (ImGui.IsItemHovered() && !ImGui.IsPopupOpen(popupId))
        {
            var lines = new List<string> { tooltip, "Click for gather locations." };
            var shown = 0;
            foreach (var loc in locations)
            {
                if (shown >= 6)
                {
                    lines.Add($"...and {locations.Count - shown} more");
                    break;
                }
                lines.Add($"{loc.Name} — {loc.Territory.Name}");
                shown++;
            }
            ImGui.SetTooltip(string.Join('\n', lines));
        }

        if (clicked)
            ImGui.OpenPopup(popupId);

        DrawPopup(itemId, locations, popupId);
        return true;
    }

    private static bool IsGatherSourceIcon(uint itemId, uint iconId)
    {
        // Miner / Botanist / Fisher class job icons, or gatherable/fish items with those sources.
        if (GatherBuddy.GameData.Gatherables.ContainsKey(itemId))
            return true;
        if (GatherBuddy.GameData.Fishes.ContainsKey(itemId))
            return true;
        return false;
    }

    private static List<ILocation> ResolveLocations(uint itemId)
    {
        var list = new List<ILocation>();
        try
        {
            if (GatherBuddy.GameData.Gatherables.TryGetValue(itemId, out var gatherable))
            {
                foreach (var node in gatherable.NodeList)
                {
                    if (node is ILocation loc)
                        list.Add(loc);
                }

                // Prefer best location first if available.
                try
                {
                    var (best, _) = GatherBuddy.UptimeManager.BestLocation(gatherable);
                    if (best != null)
                    {
                        list.Remove(best);
                        list.Insert(0, best);
                    }
                }
                catch
                {
                    // ignore
                }
            }
            else if (GatherBuddy.GameData.Fishes.TryGetValue(itemId, out var fish))
            {
                foreach (var spot in fish.FishingSpots)
                {
                    if (spot is ILocation loc)
                        list.Add(loc);
                }

                try
                {
                    var (best, _) = GatherBuddy.UptimeManager.BestLocation(fish);
                    if (best != null)
                    {
                        list.Remove(best);
                        list.Insert(0, best);
                    }
                }
                catch
                {
                    // ignore
                }
            }
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Debug($"[GatherSourcePopup] ResolveLocations failed for {itemId}: {ex.Message}");
        }

        // Distinct by territory + name.
        return list
            .GroupBy(l => (l.Territory.Id, l.Name))
            .Select(g => g.First())
            .ToList();
    }

    private static void DrawPopup(uint itemId, IReadOnlyList<ILocation> locations, string popupId)
    {
        if (!ImGui.BeginPopup(popupId))
            return;

        ImGui.TextColored(new Vector4(0.65f, 0.65f, 0.70f, 1f),
            $"{locations.Count} location{(locations.Count == 1 ? "" : "s")}");

        var zoneGroups = locations
            .GroupBy(l => (TerritoryId: l.Territory.Id, ZoneName: l.Territory.Name))
            .OrderBy(g => g.Key.ZoneName)
            .ToList();

        var maxH = VulcanUiScaling.Scaled(320f);
        ImGui.BeginChild($"{popupId}_scroll", new Vector2(VulcanUiScaling.Scaled(420f), Math.Min(maxH, zoneGroups.Count * 48f + 40f)), false);

        for (var zi = 0; zi < zoneGroups.Count; zi++)
        {
            var zg = zoneGroups[zi];
            var stateId = $"{popupId}_z_{zg.Key.TerritoryId}_{zi}";
            if (!ZoneOpenStates.ContainsKey(stateId))
                ZoneOpenStates[stateId] = zi == 0;

            var primary = zg.FirstOrDefault(l => l.ClosestAetheryte != null) ?? zg.First();
            var open = ZoneOpenStates[stateId];

            // Zone header row with flag + TP before the collapsible header hitbox.
            DrawIconButton($"flag_z_{zi}", FontAwesomeIcon.MapMarkerAlt, MarkerButtonColor,
                "Place map marker for this zone.",
                () => PlaceFlag(primary));
            ImGui.SameLine(0, 4);
            var aetheryte = primary.ClosestAetheryte;
            if (aetheryte != null)
            {
                DrawIconButton($"tp_z_{zi}", FontAwesomeIcon.Running, TeleportButtonColor,
                    "Teleport to nearest aetheryte and place map marker.",
                    () =>
                    {
                        PlaceFlag(primary);
                        Executor.TeleportToAetheryte(aetheryte);
                    });
            }
            else
            {
                DrawIconButton($"tp_z_{zi}", FontAwesomeIcon.Running, TeleportButtonColor,
                    "No aetheryte available.", null, disabled: true);
            }

            ImGui.SameLine(0, 6);
            open = ImGui.CollapsingHeader($"{zg.Key.ZoneName} ({zg.Count()})###{stateId}", open ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None);
            ZoneOpenStates[stateId] = open;

            if (open)
            {
                foreach (var loc in zg)
                {
                    ImGui.Indent();
                    DrawIconButton($"flag_{loc.Id}_{loc.Name}", FontAwesomeIcon.MapMarkerAlt, MarkerButtonColor,
                        "Place map marker.",
                        () => PlaceFlag(loc));
                    ImGui.SameLine(0, 4);
                    if (loc.ClosestAetheryte != null)
                    {
                        var ae = loc.ClosestAetheryte;
                        DrawIconButton($"tp_{loc.Id}_{loc.Name}", FontAwesomeIcon.Running, TeleportButtonColor,
                            "Teleport and place map marker.",
                            () =>
                            {
                                PlaceFlag(loc);
                                Executor.TeleportToAetheryte(ae);
                            });
                    }
                    else
                    {
                        DrawIconButton($"tp_{loc.Id}_{loc.Name}", FontAwesomeIcon.Running, TeleportButtonColor,
                            "No aetheryte available.", null, disabled: true);
                    }

                    ImGui.SameLine(0, 6);
                    var coord = loc.IntegralXCoord > 0
                        ? $"{loc.IntegralXCoord / 100f:F1}, {loc.IntegralYCoord / 100f:F1}"
                        : "—";
                    ImGui.TextUnformatted($"{loc.Name}  ({coord})");
                    ImGui.Unindent();
                }
            }
        }

        ImGui.EndChild();
        ImGui.EndPopup();
    }

    private static void PlaceFlag(ILocation location)
    {
        try
        {
            var mapX = location.IntegralXCoord / 100f;
            var mapY = location.IntegralYCoord / 100f;
            var mapId = location.Territory.Data.Map.RowId;
            var payload = new MapLinkPayload(location.Territory.Id, mapId, mapX, mapY);
            Dalamud.GameGui.OpenMapWithMapLink(payload);
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Warning($"[GatherSourcePopup] Failed to place flag for {location.Name}: {ex.Message}");
        }
    }

    private static void DrawIconButton(string id, FontAwesomeIcon icon, Vector4 color, string tooltip, Action? onClick, bool disabled = false)
    {
        var size = Vector2.One * ImGui.GetFrameHeight();
        using var font = ImRaii.PushFont(UiBuilder.IconFont);
        var iconText = icon.ToIconString();
        var cursor = ImGui.GetCursorScreenPos();
        var iconSize = ImGui.CalcTextSize(iconText);

        bool clicked;
        using (ImRaii.PushId(id))
        {
            if (disabled)
                ImGui.BeginDisabled();
            clicked = ImGui.Button(string.Empty, size);
            if (disabled)
                ImGui.EndDisabled();
        }

        var iconPos = cursor + ((size - iconSize) / 2f);
        ImGui.GetWindowDrawList().AddText(iconPos, ImGui.GetColorU32(color), iconText);

        if (ImGui.IsItemHovered(disabled ? ImGuiHoveredFlags.AllowWhenDisabled : ImGuiHoveredFlags.None))
            ImGui.SetTooltip(tooltip);

        if (clicked && !disabled && onClick != null)
        {
            try { onClick(); }
            catch (Exception ex)
            {
                GatherBuddy.Log.Warning($"[GatherSourcePopup] Action failed: {ex.Message}");
            }
        }
    }
}
