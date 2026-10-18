# Retainer Undercut

Dalamud plugin that checks each retainer listing against the market board and undercuts the
cheapest competitor by a configurable amount (default 1 gil). Large drops or drops below a
per-item floor require confirmation.

## Setup

Requires the .NET 10 SDK and XIVLauncher (with Dalamud) installed and run at least once,
so the Dalamud dev files exist under `%AppData%\XIVLauncher\addon\Hooks\dev`.

```
git clone --recurse-submodules <your repo url>
dotnet build RetainerUndercut/RetainerUndercut.csproj -c Release
```
Then add `RetainerUndercut/bin/Release` under `/xlsettings` → Experimental → Dev Plugin Locations.

## Usage

- **With AutoRetainer:** leave "Run as AutoRetainer postprocess task" on. After AutoRetainer
  handles a retainer's venture, it hands that retainer to this plugin, which reprices and hands it back.
- **Standalone:** at a summoning bell, `/undercut run` with the retainer list open does every retainer;
  with one retainer open it does just that one. `/undercut stop` aborts.

## Pricing rules

1. Lowest non-own listing (HQ-only for HQ items by default) minus `UndercutAmount`.
2. If that's below the item's floor **or** drops more than `MaxDropPercent` in one step → confirmation.
3. Confirmation behaviour: `Ask` (popup, falls back after timeout), `Skip` (goes to Pending Review), or
   `ClampToFloor`.

## ⚠ Verify before trusting it with real listings

These UI callback values are from community knowledge and may differ by patch. Check each with
`/xldata` → Addon Inspector and the Callback logger, ideally on a cheap test item:

| Where | Code | Expected |
|---|---|---|
| `RetainerSellList` | `Callback.Fire(addon, true, 0, index, 1)` | Opens context menu for row |
| `ContextMenu` | `Callback.Fire(cm, true, 0, 0, 0, 0, 0)` | "Adjust Price" |
| `RetainerSell` | `Callback.Fire(addon, true, 4)` | Compare Prices |
| `RetainerSell` | `Callback.Fire(addon, true, 0)` / `1` | Confirm / Cancel |
| `RetainerSell` | `PriceInputIndex = 0` | First NumericInput is the asking price |

Also confirm the sell list shows items in `RetainerMarket` slot order. If not, the plugin will
log "item mismatch" and skip rather than misprice, but you'll want to fix the mapping.
