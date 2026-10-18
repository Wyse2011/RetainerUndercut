using System;
using System.Collections.Generic;

namespace RetainerUndercut;

public enum DecisionKind { Keep, Change, NeedsConfirmation }

public record Decision(DecisionKind Kind, int CurrentPrice, int? LowestCompetitor, int ProposedPrice, string Reason);

/// <summary>
/// No game code in here on purpose, so it's easy to unit test.
/// </summary>
public static class PriceDecision
{
    public static Decision Decide(int current, int? lowestCompetitor, ItemRule rule, Configuration c)
    {
        if (!rule.Enabled)
            return new(DecisionKind.Keep, current, lowestCompetitor, current, "Rule disabled for this item");

        if (lowestCompetitor is not int lowest)
            return new(DecisionKind.Keep, current, null, current, "No competing listings");

        var target = Math.Max(1, lowest - Math.Max(0, c.UndercutAmount));

        // We're already the cheapest.
        if (lowest > current)
        {
            if (!c.RaiseWhenCheapest || target <= current)
                return new(DecisionKind.Keep, current, lowest, current, "Already cheapest");
            return new(DecisionKind.Change, current, lowest, target, $"Raising to {target:N0} (next seller at {lowest:N0})");
        }

        if (target >= current)
            return new(DecisionKind.Keep, current, lowest, current, "No change needed");

        var reasons = new List<string>();

        if (rule.MinPrice > 0 && target < rule.MinPrice)
            reasons.Add($"{target:N0} is below your floor of {rule.MinPrice:N0}");

        var dropPct = (current - target) / (double)current * 100.0;
        if (c.MaxDropPercent > 0 && dropPct > c.MaxDropPercent)
            reasons.Add($"price would drop {dropPct:0.#}% ({current:N0} → {target:N0})");

        if (reasons.Count > 0)
            return new(DecisionKind.NeedsConfirmation, current, lowest, target, string.Join("; ", reasons));

        return new(DecisionKind.Change, current, lowest, target, $"Undercutting {lowest:N0}");
    }

    /// <summary>The price to use when ClampToFloor applies, or null to skip.</summary>
    public static int? ClampPrice(Decision d, ItemRule rule)
    {
        if (rule.MinPrice > 0 && rule.MinPrice < d.CurrentPrice)
            return Math.Max(rule.MinPrice, d.ProposedPrice);
        return null;
    }
}
