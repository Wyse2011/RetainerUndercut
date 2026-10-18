using Dalamud.Game.Network.Structures;
using Dalamud.Plugin.Services;
using ECommons.Logging;
using FFXIVClientStructs.FFXIV.Client.Game;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RetainerUndercut;

/// <summary>
/// Listens for market board offerings (sent when "Compare Prices" opens) and extracts the
/// cheapest listing that isn't one of your own retainers.
/// </summary>
public unsafe class MarketWatcher : IDisposable
{
    private readonly IMarketBoard marketBoard;
    private readonly Configuration config;

    private uint expectedItemId;
    private bool expectedHq;
    private HashSet<ulong> ownRetainers = new();

    public bool Waiting { get; private set; }
    public bool HasResult { get; private set; }
    public int? LowestCompetitor { get; private set; }
    public bool ItemMismatch { get; private set; }

    public MarketWatcher(IMarketBoard marketBoard, Configuration config)
    {
        this.marketBoard = marketBoard;
        this.config = config;
        marketBoard.OfferingsReceived += OnOfferings;
    }

    public void Begin(uint itemId, bool hq)
    {
        expectedItemId = itemId;
        expectedHq = hq;
        HasResult = false;
        ItemMismatch = false;
        LowestCompetitor = null;
        ownRetainers = GetOwnRetainerIds();
        Waiting = true;
    }

    public void Cancel() => Waiting = false;

    private void OnOfferings(IMarketBoardCurrentOfferings offerings)
    {
        // Listings arrive in pages of 10, cheapest first. We only need the first page.
        if (!Waiting) return;
        Waiting = false;

        var listings = offerings.ItemListings;
        if (listings.Count > 0 && listings[0].ItemId != expectedItemId)
        {
            // Sell-list order didn't match what we expected. Bail on this item rather than misprice it.
            PluginLog.Warning($"Market data was for item {listings[0].ItemId}, expected {expectedItemId}. Skipping.");
            ItemMismatch = true;
            HasResult = true;
            return;
        }

        IEnumerable<IMarketBoardItemListing> competitors = listings.Where(l => !ownRetainers.Contains(l.RetainerId));
        if (expectedHq && config.HqOnlyComparesHq)
            competitors = competitors.Where(l => l.IsHq);

        var lowest = competitors.Select(l => (int)l.PricePerUnit).DefaultIfEmpty(-1).Min();
        LowestCompetitor = lowest < 0 ? null : lowest;
        HasResult = true;
    }

    private static HashSet<ulong> GetOwnRetainerIds()
    {
        var set = new HashSet<ulong>();
        var rm = RetainerManager.Instance();
        if (rm == null) return set;
        for (uint i = 0; i < rm->GetRetainerCount(); i++)
        {
            var r = rm->GetRetainerBySortedIndex(i);
            if (r != null && r->RetainerId != 0) set.Add(r->RetainerId);
        }
        return set;
    }

    public void Dispose() => marketBoard.OfferingsReceived -= OnOfferings;
}
