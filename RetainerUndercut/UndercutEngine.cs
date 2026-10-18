using ECommons;
using ECommons.Automation;
using ECommons.Automation.NeoTaskManager;
using ECommons.DalamudServices;
using ECommons.Logging;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using LuminaItem = Lumina.Excel.Sheets.Item;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RetainerUndercut;

public class PendingConfirmation
{
    public required string Retainer;
    public required string ItemName;
    public required ItemRule Rule;
    public required Decision Decision;
    public DateTime Deadline;
    public bool Resolved;
    public int? ChosenPrice; // null = skip
}

public record ReviewEntry(DateTime Time, string Retainer, string ItemName, int Current, int? Lowest, int Proposed, string Reason);

public unsafe class UndercutEngine : IDisposable
{
    private readonly Configuration C;
    private readonly MarketWatcher market;
    private readonly TaskManager tm = new(new(timeLimitMS: 20000, abortOnTimeout: true));

    public PendingConfirmation? Pending { get; private set; }
    public readonly List<ReviewEntry> Review = new();
    public readonly List<string> Log = new();

    /// <summary>Called once the UI is back where we found it (used to hand control back to AutoRetainer).</summary>
    private Action? onFinished;
    private bool cleaningUp;

    public bool IsBusy => tm.IsBusy || onFinished != null;

    public UndercutEngine(Configuration config, MarketWatcher market)
    {
        C = config;
        this.market = market;
        Svc.Framework.Update += Watchdog;
    }

    // ───────────────────────── entry points ─────────────────────────

    /// <summary>Process the retainer whose menu (SelectString) or sell list is currently open.</summary>
    public void ProcessCurrentRetainer(string retainerName, Action? whenDone = null)
    {
        if (IsBusy) { Print("Already running."); whenDone?.Invoke(); return; }
        onFinished = whenDone ?? (() => { });
        EnqueueRetainer(retainerName, closeSellListAfter: whenDone != null);
    }

    /// <summary>Standalone: start from the summoning-bell retainer list and do every retainer with listings.</summary>
    public void ProcessAllRetainers()
    {
        if (IsBusy) { Print("Already running."); return; }
        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerList", out var rl) || !GenericHelpers.IsAddonReady(rl))
        {
            Print("Open the retainer list at a summoning bell first.");
            return;
        }
        onFinished = () => Print("Finished all retainers.");

        foreach (var name in RetainersWithListings())
        {
            if (C.ExcludedRetainers.Contains(name)) continue;
            var n = name;
            tm.Enqueue(() => SelectRetainer(n), $"Select {n}");
            tm.Enqueue(SkipTalkUntilMenu, "Wait for retainer menu");
            EnqueueRetainer(n, closeSellListAfter: true);
            tm.Enqueue(QuitRetainerMenu, "Quit retainer menu");
            tm.Enqueue(SkipTalkUntilRetainerList, "Back to retainer list");
            tm.EnqueueDelay(500);
        }
    }

    public void Abort()
    {
        tm.Abort();
        market.Cancel();
        if (Pending != null) { Pending.Resolved = true; Pending.ChosenPrice = null; }
        onFinished ??= () => { };
        Print("Stopped.");
    }

    public static bool RetainerHasListings(string name)
    {
        var rm = RetainerManager.Instance();
        if (rm == null) return false;
        for (uint i = 0; i < rm->GetRetainerCount(); i++)
        {
            var r = rm->GetRetainerBySortedIndex(i);
            if (r != null && r->NameString == name) return r->MarketItemCount > 0;
        }
        return false;
    }

    // ───────────────────────── per-retainer flow ─────────────────────────

    private void EnqueueRetainer(string retainer, bool closeSellListAfter)
    {
        tm.Enqueue(OpenSellList, "Open sell list");
        tm.EnqueueDelay(500);
        tm.Enqueue(() =>
        {
            var items = ReadListings();
            AddLog($"{retainer}: {items.Count} listing(s)");
            for (var i = 0; i < items.Count; i++)
                EnqueueItem(retainer, i, items[i]);
            if (closeSellListAfter)
                tm.Enqueue(() => CloseAddon("RetainerSellList"), "Close sell list");
        }, "Read listings");
    }

    private record Listing(int SellListIndex, uint ItemId, bool Hq);

    private class ItemJob
    {
        public int CurrentPrice;
        public Decision? Decision;
        public int? FinalPrice;
    }

    private void EnqueueItem(string retainer, int index, Listing item)
    {
        var job = new ItemJob();
        var rule = GetOrCreateRule(item.ItemId, item.Hq);

        tm.Enqueue(() => ClickSellListItem(index), $"Open item {index}");
        tm.Enqueue(ClickAdjustPrice, "Adjust price");
        tm.Enqueue(() =>
        {
            if (!TryGetReady("RetainerSell", out var addon)) return false;
            var input = GetPriceInput(addon);
            if (input == null) return false;
            job.CurrentPrice = input->Value;
            return true;
        }, "Read current price");

        tm.Enqueue(() =>
        {
            if (!TryGetReady("RetainerSell", out var addon)) return false;
            market.Begin(item.ItemId, item.Hq);
            Callback.Fire(addon, true, 4); // "Compare Prices"  ← verify index
            return true;
        }, "Compare prices");

        tm.Enqueue(() => market.HasResult && TryGetReady("ItemSearchResult", out _), "Wait for market data");

        tm.Enqueue(() =>
        {
            CloseAddon("ItemSearchResult");
            rule.LastChecked = DateTime.Now;
            if (market.ItemMismatch)
            {
                job.Decision = new(DecisionKind.Keep, job.CurrentPrice, null, job.CurrentPrice, "Item mismatch, skipped");
                return true;
            }
            job.Decision = PriceDecision.Decide(job.CurrentPrice, market.LowestCompetitor, rule, C);
            job.FinalPrice = job.Decision.Kind == DecisionKind.Change ? job.Decision.ProposedPrice : null;

            if (job.Decision.Kind == DecisionKind.NeedsConfirmation)
                StartConfirmation(retainer, rule, job.Decision, C.ConfirmBehavior);
            return true;
        }, "Decide");

        // Waits for you if a confirmation is open. Own timeout handling, so the task itself never times out.
        tm.Enqueue(() =>
        {
            if (Pending == null) return true;
            if (!Pending.Resolved && DateTime.Now > Pending.Deadline)
            {
                AddLog($"No answer for {Pending.ItemName}, using fallback: {C.TimeoutFallback}");
                ResolveByBehavior(Pending, C.TimeoutFallback == ConfirmBehavior.Ask ? ConfirmBehavior.Skip : C.TimeoutFallback);
            }
            if (!Pending.Resolved) return false;
            job.FinalPrice = Pending.ChosenPrice;
            Pending = null;
            return true;
        }, "Wait for confirmation", new(timeLimitMS: int.MaxValue));

        tm.Enqueue(() =>
        {
            if (!TryGetReady("RetainerSell", out var addon)) return false;
            var name = rule.Name;
            if (job.FinalPrice is int p && p != job.CurrentPrice)
            {
                var input = GetPriceInput(addon);
                if (input == null) return false;
                input->SetValue(p);
                Callback.Fire(addon, true, 0); // Confirm  ← verify index
                rule.LastSetPrice = p;
                AddLog($"{retainer}: {name} {job.CurrentPrice:N0} → {p:N0} ({job.Decision?.Reason})");
            }
            else
            {
                Callback.Fire(addon, true, 1); // Cancel  ← verify index
                AddLog($"{retainer}: {name} kept at {job.CurrentPrice:N0} ({job.Decision?.Reason})");
            }
            C.Save();
            return true;
        }, "Apply price");

        tm.Enqueue(() => !GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSell", out var a) || !a->IsVisible, "Wait for sell window close");
        tm.EnqueueDelay(C.DelayBetweenItemsMs);
    }

    // ───────────────────────── confirmation ─────────────────────────

    private void StartConfirmation(string retainer, ItemRule rule, Decision d, ConfirmBehavior behavior)
    {
        Pending = new PendingConfirmation
        {
            Retainer = retainer,
            ItemName = rule.Name,
            Rule = rule,
            Decision = d,
            Deadline = DateTime.Now.AddSeconds(Math.Max(5, C.ConfirmTimeoutSeconds)),
        };
        if (behavior == ConfirmBehavior.Ask)
        {
            Print($"Confirmation needed for {rule.Name}: {d.Reason}");
            return; // ConfirmWindow picks it up
        }
        ResolveByBehavior(Pending, behavior);
    }

    private void ResolveByBehavior(PendingConfirmation p, ConfirmBehavior behavior)
    {
        p.ChosenPrice = behavior == ConfirmBehavior.ClampToFloor ? PriceDecision.ClampPrice(p.Decision, p.Rule) : null;
        if (p.ChosenPrice == null)
            Review.Add(new(DateTime.Now, p.Retainer, p.ItemName, p.Decision.CurrentPrice, p.Decision.LowestCompetitor, p.Decision.ProposedPrice, p.Decision.Reason));
        p.Resolved = true;
    }

    /// <summary>Called by the confirm window.</summary>
    public void Resolve(int? price)
    {
        if (Pending == null) return;
        if (price == null)
            Review.Add(new(DateTime.Now, Pending.Retainer, Pending.ItemName, Pending.Decision.CurrentPrice, Pending.Decision.LowestCompetitor, Pending.Decision.ProposedPrice, Pending.Decision.Reason + " (skipped by you)"));
        Pending.ChosenPrice = price;
        Pending.Resolved = true;
    }

    // ───────────────────────── UI steps ─────────────────────────

    private bool? OpenSellList()
    {
        if (TryGetReady("RetainerSellList", out _)) return true;
        if (!TryGetReady("SelectString", out var ss)) return false;
        if (!EzThrottler.Throttle("RU.SelectString", 1000)) return false;
        var entry = new AddonMaster.SelectString(ss).Entries.FirstOrDefault(e => e.Text.Contains(C.SellMenuText, StringComparison.OrdinalIgnoreCase));
        if (entry.Text == null) { PluginLog.Warning("Sell menu entry not found; check SellMenuText."); return false; }
        entry.Select();
        return false;
    }

    private List<Listing> ReadListings()
    {
        // Assumes the sell list shows items in RetainerMarket slot order. MarketWatcher verifies the item ID
        // returned by the server, so a mismatch skips the item instead of mispricing it.
        var result = new List<Listing>();
        var inv = InventoryManager.Instance()->GetInventoryContainer(InventoryType.RetainerMarket);
        if (inv == null) return result;
        for (var i = 0; i < inv->Size; i++)
        {
            var slot = inv->GetInventorySlot(i);
            if (slot == null || slot->ItemId == 0) continue;
            result.Add(new(result.Count, slot->ItemId, slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality)));
        }
        return result;
    }

    private bool? ClickSellListItem(int index)
    {
        if (!TryGetReady("RetainerSellList", out var addon)) return false;
        Callback.Fire(addon, true, 0, index, 1); // right-click row  ← verify
        return true;
    }

    private bool? ClickAdjustPrice()
    {
        if (!TryGetReady("ContextMenu", out var cm)) return false;
        Callback.Fire(cm, true, 0, 0, 0, 0, 0); // first entry = "Adjust Price"  ← verify
        return true;
    }

    private bool? SelectRetainer(string name)
    {
        if (!TryGetReady("RetainerList", out var rl)) return false;
        if (!EzThrottler.Throttle("RU.RetainerList", 1000)) return false;
        var r = new AddonMaster.RetainerList(rl).Retainers.FirstOrDefault(x => x.Name == name);
        if (r.Name == null) return false;
        r.Select();
        return true;
    }

    private bool? SkipTalkUntilMenu()
    {
        if (TryGetReady("SelectString", out _)) return true;
        if (TryGetReady("Talk", out var t) && EzThrottler.Throttle("RU.Talk", 200)) new AddonMaster.Talk(t).Click();
        return false;
    }

    private bool? QuitRetainerMenu()
    {
        if (!TryGetReady("SelectString", out var ss)) return false;
        if (!EzThrottler.Throttle("RU.Quit", 1000)) return false;
        var entries = new AddonMaster.SelectString(ss).Entries;
        entries[^1].Select(); // last entry is "Quit"
        return true;
    }

    private bool? SkipTalkUntilRetainerList()
    {
        if (TryGetReady("RetainerList", out _)) return true;
        if (TryGetReady("Talk", out var t) && EzThrottler.Throttle("RU.Talk", 200)) new AddonMaster.Talk(t).Click();
        return false;
    }

    // ───────────────────────── watchdog / cleanup ─────────────────────────

    /// <summary>
    /// Runs every frame. Once the task queue is empty (finished OR aborted), closes any windows we
    /// left open and then hands control back. This guarantees AutoRetainer is never left hanging.
    /// </summary>
    private void Watchdog(object _)
    {
        if (onFinished == null || tm.IsBusy) return;

        string[] ours = ["ItemSearchResult", "ContextMenu", "RetainerSell"];
        foreach (var name in ours)
        {
            if (GenericHelpers.TryGetAddonByName<AtkUnitBase>(name, out var a) && a->IsVisible)
            {
                if (EzThrottler.Throttle("RU.Cleanup", 300)) CloseAddon(name);
                cleaningUp = true;
                return;
            }
        }
        // When handing back to AutoRetainer, it expects the retainer menu, not the sell list.
        if (cleaningUp && GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSellList", out var sl) && sl->IsVisible)
        {
            if (EzThrottler.Throttle("RU.Cleanup", 300)) CloseAddon("RetainerSellList");
            return;
        }

        cleaningUp = false;
        var cb = onFinished;
        onFinished = null;
        GenericHelpers.Safe(() => cb());
    }

    // ───────────────────────── helpers ─────────────────────────

    private static bool TryGetReady(string name, out AtkUnitBase* addon)
        => GenericHelpers.TryGetAddonByName(name, out addon) && GenericHelpers.IsAddonReady(addon);

    private static bool CloseAddon(string name)
    {
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>(name, out var a) && a->IsVisible) a->Close(true);
        return true;
    }

    private AtkComponentNumericInput* GetPriceInput(AtkUnitBase* addon)
    {
        var found = new List<nint>();
        for (var i = 0; i < addon->UldManager.NodeListCount; i++)
        {
            var node = addon->UldManager.NodeList[i];
            if (node == null || (int)node->Type < 1000) continue;
            var comp = ((AtkComponentNode*)node)->Component;
            if (comp == null) continue;
            var info = (AtkUldComponentInfo*)comp->UldManager.Objects;
            if (info != null && info->ComponentType == ComponentType.NumericInput) found.Add((nint)comp);
        }
        // NodeList is stored back-to-front; reverse so index 0 is the topmost (asking price).
        found.Reverse();
        return C.PriceInputIndex < found.Count ? (AtkComponentNumericInput*)found[C.PriceInputIndex] : null;
    }

    private ItemRule GetOrCreateRule(uint itemId, bool hq)
    {
        var key = ItemRule.Key(itemId, hq);
        if (!C.Rules.TryGetValue(key, out var rule))
        {
            var name = Svc.Data.GetExcelSheet<LuminaItem>()?.GetRowOrDefault(itemId)?.Name.ToString() ?? $"Item {itemId}";
            rule = new ItemRule { ItemId = itemId, Hq = hq, Name = name + (hq ? " (HQ)" : "") };
            C.Rules[key] = rule;
        }
        return rule;
    }

    private static List<string> RetainersWithListings()
    {
        var list = new List<string>();
        var rm = RetainerManager.Instance();
        for (uint i = 0; i < rm->GetRetainerCount(); i++)
        {
            var r = rm->GetRetainerBySortedIndex(i);
            if (r != null && r->Available && r->MarketItemCount > 0) list.Add(r->NameString);
        }
        return list;
    }

    private void AddLog(string s)
    {
        Log.Add($"[{DateTime.Now:HH:mm:ss}] {s}");
        if (Log.Count > 500) Log.RemoveAt(0);
        PluginLog.Information(s);
    }

    private static void Print(string s) => Svc.Chat.Print($"[Undercut] {s}");

    public void Dispose()
    {
        Svc.Framework.Update -= Watchdog;
        tm.Abort();
    }
}
