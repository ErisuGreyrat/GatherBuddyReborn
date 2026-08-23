# Private GBR fork — GC Supply + Materials Route + Gather popups

Surgical private fork of GatherBuddyReborn. No separate NeekoGc plugin; behaviour lives in this tree.

## Settings (Vulcan → Settings)

| Setting | Default | Effect |
|--------|---------|--------|
| **GC Supply → Vulcan list overlay** | on | When `GrandCompanySupplyList` is open, draws a bar with **Create Vulcan list from supply**. Creates a normal Vulcan list from craftable commitments and opens it. |
| **Materials: prefer Route tab by default** | off | Materials window opens on the **Route** tab. |
| **Route: hide fully covered rows by default** | on | Route omits items fully covered by inventory + retainer (Allagan Tools optional; retainers = 0 if off). |

## How to use

1. **GC supply → list** — Open GC supply list, click **Create Vulcan list from supply**.
2. **Gather/fish source icons** — Click Botanist/Miner/Fisher icons on Materials Classic rows for location popup (flag + TP).
3. **Route tab** — Materials → Route: missing gatherable/fish grouped by zone/aetheryte with TP buttons.

See full file list and architecture notes in this document after the code lands.
