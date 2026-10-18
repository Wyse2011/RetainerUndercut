using Dalamud.Configuration;
using System;
using System.Collections.Generic;

namespace RetainerUndercut;

/// <summary>What to do when a proposed price trips the floor or the max-drop rule.</summary>
public enum ConfirmBehavior
{
    /// <summary>Pop up a window and wait for you (falls back to TimeoutFallback after ConfirmTimeoutSeconds).</summary>
    Ask,
    /// <summary>Leave the listing alone and add it to the Pending Review list.</summary>
    Skip,
    /// <summary>Drop to your floor price instead (only if a floor is set and it's below the current price), else skip.</summary>
    ClampToFloor,
}

[Serializable]
public class ItemRule
{
    public uint ItemId;
    public bool Hq;
    public string Name = "";
    public bool Enabled = true;
    /// <summary>Never go below this without confirmation. 0 = no floor.</summary>
    public int MinPrice;
    public int? LastSetPrice;
    public DateTime? LastChecked;

    public static string Key(uint itemId, bool hq) => $"{itemId}:{(hq ? 1 : 0)}";
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public bool Enabled = true;
    public bool IntegrateWithAutoRetainer = true;

    public int UndercutAmount = 1;

    /// <summary>A single change that drops the price by more than this percent needs confirmation.</summary>
    public float MaxDropPercent = 50f;

    /// <summary>HQ listings only compete with HQ listings.</summary>
    public bool HqOnlyComparesHq = true;

    /// <summary>If the cheapest competitor is above you, raise your price to (competitor - UndercutAmount).</summary>
    public bool RaiseWhenCheapest = false;

    public ConfirmBehavior ConfirmBehavior = ConfirmBehavior.Ask;
    public ConfirmBehavior TimeoutFallback = ConfirmBehavior.Skip;
    public int ConfirmTimeoutSeconds = 90;

    /// <summary>The market board throttles rapid price checks. Keep this at 2000+.</summary>
    public int DelayBetweenItemsMs = 2500;

    /// <summary>Text of the retainer menu entry that opens the sell list. Change for non-English clients.</summary>
    public string SellMenuText = "Sell items in your retainer's inventory on the market";

    /// <summary>Which NumericInput on the RetainerSell window is the asking price (verify with /xldata).</summary>
    public int PriceInputIndex = 0;

    public List<string> ExcludedRetainers = new();
    public Dictionary<string, ItemRule> Rules = new();

    public void Save() => ECommons.DalamudServices.Svc.PluginInterface.SavePluginConfig(this);
}
