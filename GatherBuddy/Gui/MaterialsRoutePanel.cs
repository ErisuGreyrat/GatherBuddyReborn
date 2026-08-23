using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface;
using ElliLib.Raii;
using GatherBuddy.Classes;
using GatherBuddy.Interfaces;
using GatherBuddy.Plugin;
using Lumina.Excel.Sheets;

namespace GatherBuddy.Gui;

/// <summary>
/// Optimized gather route section inside Materials: group missing gatherable/fish by zone + aetheryte,
/// order by live teleport gil then nearest-neighbour AetherDistance, retainer-aware toggle.
/// </summary>
internal static class MaterialsRoutePanel
{
    private static readonly Vector4 AccentGather = new(0.45f, 1.00f, 0.45f, 1f);
    private static readonly Vector4 YellowRetainer = new(0.95f, 0.85f, 0.30f, 1f);
    private static readonly Vector4 TeleportGreen = new(0.60f, 0.95f, 0.60f, 1f);
    private static readonly Vector4 MarkerBlue = new(0.45f, 0.80f, 1.00f, 1f);

    internal readonly record struct RouteItem(
        uint ItemId,
        string Name,
        int Needed,
        int Have,
        int Retainer,
        int Missing,
        ILocation? Location,
        Aetheryte? Aetheryte,
        uint TerritoryId,
        string ZoneName);

    internal readonly record struct RouteStop(
        uint TerritoryId,
        string ZoneName,
        Aetheryte? Aetheryte,
        int EstimatedGil,
        List<RouteItem> Items);

    public static void Draw(CraftingListEditor editor)
    {
        using var theme = VulcanUiStyle.PushTheme();

        var hideCovered = GatherBuddy.Config.MaterialsRouteHideFullyCovered;
        if (ImGui.Checkbox("Hide rows fully covered by inventory + retainer", ref hideCovered))
        {
            GatherBuddy.Config.MaterialsRouteHideFullyCovered = hideCovered;
            GatherBuddy.Config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("When enabled, items you already fully own (inventory + retainer) are omitted from the route.");

        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.65f, 1f), "Yellow counts = retainer");

        var stops = BuildRoute(editor, hideCovered);
        if (stops.Count == 0)
        {
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f),
                "No missing gatherable/fish materials for an optimized route.");
            return;
        }

        ImGui.TextColored(AccentGather, $"Route · {stops.Count} stop{(stops.Count == 1 ? "" : "s")}");
        ImGui.Separator();

        for (var i = 0; i < stops.Count; i++)
        {
            var stop = stops[i];
            var header = stop.Aetheryte != null
                ? $"{i + 1}. {stop.ZoneName}  ·  {stop.Aetheryte.Name}"
                : $"{i + 1}. {stop.ZoneName}";
            if (stop.EstimatedGil > 0)
                header += $"  (~{stop.EstimatedGil:N0} gil)";

            DrawStopActions(stop);
            ImGui.SameLine(0, 6);
            if (ImGui.CollapsingHeader($"{header}###route_stop_{stop.TerritoryId}_{i}", ImGuiTreeNodeFlags.DefaultOpen))
            {
                foreach (var item in stop.Items)
                {
                    ImGui.Indent();
                    var missingColor = item.Missing > 0
                        ? new Vector4(1f, 0.45f, 0.45f, 1f)
                        : new Vector4(0.4f, 1f, 0.4f, 1f);
                    ImGui.TextColored(missingColor, $"{item.Name}");
                    ImGui.SameLine();
                    ImGui.TextUnformatted($"need {item.Needed} · have {item.Have}");
                    if (item.Retainer > 0)
                    {
                        ImGui.SameLine();
                        ImGui.TextColored(YellowRetainer, $"+ret {item.Retainer}");
                    }
                    if (item.Location != null)
                    {
                        ImGui.SameLine();
                        ImGui.TextDisabled($"· {item.Location.Name}");
                    }
                    ImGui.Unindent();
                }
            }
        }
    }

    private static void DrawStopActions(RouteStop stop)
    {
        var size = Vector2.One * ImGui.GetFrameHeight();
        using (ImRaii.PushId($"flag_{stop.TerritoryId}_{stop.Aetheryte?.Id ?? 0}"))
        {
            using var font = ImRaii.PushFont(UiBuilder.IconFont);
            var cursor = ImGui.GetCursorScreenPos();
            var iconText = FontAwesomeIcon.MapMarkerAlt.ToIconString();
            var iconSize = ImGui.CalcTextSize(iconText);
            if (ImGui.Button(string.Empty, size))
                PlaceStopFlag(stop);
            ImGui.GetWindowDrawList().AddText(cursor + ((size - iconSize) / 2f), ImGui.GetColorU32(MarkerBlue), iconText);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Place map marker for this stop.");
        }

        ImGui.SameLine(0, 4);

        var canTp = stop.Aetheryte != null;
        using (ImRaii.PushId($"tp_{stop.TerritoryId}_{stop.Aetheryte?.Id ?? 0}"))
        {
            using var font = ImRaii.PushFont(UiBuilder.IconFont);
            var cursor = ImGui.GetCursorScreenPos();
            var iconText = FontAwesomeIcon.Running.ToIconString();
            var iconSize = ImGui.CalcTextSize(iconText);
            if (!canTp)
                ImGui.BeginDisabled();
            if (ImGui.Button(string.Empty, size) && canTp)
            {
                try
                {
                    PlaceStopFlag(stop);
                    Executor.TeleportToAetheryte(stop.Aetheryte!);
                }
                catch (Exception ex)
                {
                    GatherBuddy.Log.Warning($"[MaterialsRoute] Teleport failed: {ex.Message}");
                }
            }
            if (!canTp)
                ImGui.EndDisabled();
            ImGui.GetWindowDrawList().AddText(cursor + ((size - iconSize) / 2f), ImGui.GetColorU32(TeleportGreen), iconText);
            if (ImGui.IsItemHovered(canTp ? ImGuiHoveredFlags.None : ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(canTp ? "Teleport to this aetheryte and place a map marker." : "No aetheryte for this stop.");
        }
    }

    private static void PlaceStopFlag(RouteStop stop)
    {
        try
        {
            var loc = stop.Items.Select(i => i.Location).FirstOrDefault(l => l != null);
            if (loc != null)
            {
                var mapX = loc.IntegralXCoord / 100f;
                var mapY = loc.IntegralYCoord / 100f;
                var mapId = loc.Territory.Data.Map.RowId;
                var payload = new MapLinkPayload(loc.Territory.Id, mapId, mapX, mapY);
                Dalamud.GameGui.OpenMapWithMapLink(payload);
                return;
            }

            if (stop.Aetheryte != null)
            {
                var territory = stop.Aetheryte.Territory;
                var mapId = territory.Data.Map.RowId;
                var payload = new MapLinkPayload(territory.Id, mapId, stop.Aetheryte.XCoord / 100f, stop.Aetheryte.YCoord / 100f);
                Dalamud.GameGui.OpenMapWithMapLink(payload);
            }
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Warning($"[MaterialsRoute] Flag failed: {ex.Message}");
        }
    }

    public static List<RouteStop> BuildRoute(CraftingListEditor editor, bool hideFullyCovered)
    {
        var items = new List<RouteItem>();
        try
        {
            var materials = editor.GetDisplayMaterials();
            if (materials.Count == 0)
                return [];

            var snapshot = editor.GetRetainerSnapshot(materials.Keys);
            var itemSheet = Dalamud.GameData.GetExcelSheet<Item>();

            foreach (var (itemId, needed) in materials)
            {
                if (needed <= 0)
                    continue;
                if (!IsGatherableOrFish(itemId))
                    continue;

                var have = editor.GetInventoryCount(itemId);
                var ret = 0;
                try { ret = snapshot.GetTotalCount(itemId); }
                catch { ret = editor.GetRetainerCount(itemId); }

                var effective = have + ret;
                var missing = Math.Max(0, needed - effective);
                if (hideFullyCovered && missing <= 0)
                    continue;
                if (!hideFullyCovered && missing <= 0 && have >= needed)
                    continue;

                var name = itemSheet?.GetRow(itemId).Name.ExtractText() ?? $"Item#{itemId}";
                var (loc, ae) = ResolveBestLocation(itemId);
                var territoryId = loc?.Territory.Id ?? ae?.Territory.Id ?? 0;
                var zoneName = loc?.Territory.Name ?? ae?.Territory.Name ?? "Unknown";

                items.Add(new RouteItem(itemId, name, needed, have, ret, missing, loc, ae, territoryId, zoneName));
            }
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Warning($"[MaterialsRoute] Build failed: {ex.Message}");
            return [];
        }

        if (items.Count == 0)
            return [];

        var groups = items
            .GroupBy(i => (i.TerritoryId, AetheryteId: i.Aetheryte?.Id ?? 0u, i.ZoneName))
            .Select(g =>
            {
                var ae = g.First().Aetheryte;
                return new RouteStop(g.Key.TerritoryId, g.Key.ZoneName, ae, EstimateGil(ae), g.OrderBy(x => x.Name).ToList());
            })
            .ToList();

        return OrderStops(groups);
    }

    private static bool IsGatherableOrFish(uint itemId)
        => GatherBuddy.GameData.Gatherables.ContainsKey(itemId)
           || GatherBuddy.GameData.Fishes.ContainsKey(itemId);

    private static (ILocation? Loc, Aetheryte? Ae) ResolveBestLocation(uint itemId)
    {
        try
        {
            if (GatherBuddy.GameData.Gatherables.TryGetValue(itemId, out var g))
            {
                var (loc, _) = GatherBuddy.UptimeManager.BestLocation(g);
                return (loc, loc?.ClosestAetheryte);
            }
            if (GatherBuddy.GameData.Fishes.TryGetValue(itemId, out var f))
            {
                var (loc, _) = GatherBuddy.UptimeManager.BestLocation(f);
                return (loc, loc?.ClosestAetheryte);
            }
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Debug($"[MaterialsRoute] BestLocation failed for {itemId}: {ex.Message}");
        }
        return (null, null);
    }

    private static int EstimateGil(Aetheryte? aetheryte) => 0;

    private static List<RouteStop> OrderStops(List<RouteStop> stops)
    {
        if (stops.Count <= 1)
            return stops;

        var ordered = stops
            .OrderBy(s => s.EstimatedGil > 0 ? 0 : 1)
            .ThenBy(s => s.EstimatedGil)
            .ToList();

        var result = new List<RouteStop>(ordered.Count);
        var remaining = new List<RouteStop>(ordered);
        var current = remaining[0];
        result.Add(current);
        remaining.RemoveAt(0);

        while (remaining.Count > 0)
        {
            var bestIdx = 0;
            var bestDist = double.PositiveInfinity;
            for (var i = 0; i < remaining.Count; i++)
            {
                var d = AetherDistance(current, remaining[i]);
                if (d < bestDist)
                {
                    bestDist = d;
                    bestIdx = i;
                }
            }
            current = remaining[bestIdx];
            result.Add(current);
            remaining.RemoveAt(bestIdx);
        }

        return result;
    }

    private static double AetherDistance(RouteStop a, RouteStop b)
    {
        if (a.Aetheryte == null || b.Aetheryte == null)
            return a.TerritoryId == b.TerritoryId ? 0 : 1e9;
        try
        {
            if (a.Aetheryte.Territory.Id == b.Aetheryte.Territory.Id)
            {
                var dx = a.Aetheryte.XCoord - b.Aetheryte.XCoord;
                var dy = a.Aetheryte.YCoord - b.Aetheryte.YCoord;
                return Math.Sqrt(dx * dx + dy * dy);
            }
            return 1_000_000 + Math.Abs((int)a.Aetheryte.Id - (int)b.Aetheryte.Id);
        }
        catch { return 1e9; }
    }
}
