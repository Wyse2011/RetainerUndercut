using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using System;
using System.Numerics;

namespace RetainerUndercut.Windows;

public class ConfirmWindow : Window
{
    private readonly UndercutEngine engine;
    private readonly Configuration config;
    private int customPrice;
    private PendingConfirmation? lastSeen;

    public ConfirmWindow(UndercutEngine engine, Configuration config)
        : base("Undercut: confirm big price drop###RUConfirm", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse)
    {
        this.engine = engine;
        this.config = config;
        RespectCloseHotkey = false;
        ShowCloseButton = false;
    }

    // Shows itself whenever the engine is waiting on you.
    public override bool DrawConditions() => engine.Pending is { Resolved: false } && config.ConfirmBehavior == ConfirmBehavior.Ask;

    public override void PreDraw()
    {
        IsOpen = true;
        ImGui.SetNextWindowPos(ImGui.GetMainViewport().GetCenter(), ImGuiCond.Appearing, new Vector2(0.5f));
    }

    public override void Draw()
    {
        var p = engine.Pending!;
        var d = p.Decision;
        if (!ReferenceEquals(p, lastSeen)) { lastSeen = p; customPrice = d.ProposedPrice; }

        ImGui.TextUnformatted($"{p.ItemName}  ({p.Retainer})");
        ImGui.Separator();
        ImGui.TextUnformatted($"Your current price:   {d.CurrentPrice:N0}");
        ImGui.TextUnformatted($"Cheapest competitor:  {d.LowestCompetitor:N0}");
        ImGui.TextUnformatted($"Proposed new price:   {d.ProposedPrice:N0}");
        if (p.Rule.MinPrice > 0) ImGui.TextUnformatted($"Your floor:           {p.Rule.MinPrice:N0}");
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(1f, 0.7f, 0.3f, 1f), d.Reason);
        ImGui.Spacing();

        if (ImGui.Button($"Accept {d.ProposedPrice:N0}")) engine.Resolve(d.ProposedPrice);

        var floor = PriceDecision.ClampPrice(d, p.Rule);
        if (floor is int f)
        {
            ImGui.SameLine();
            if (ImGui.Button($"Use floor {f:N0}")) engine.Resolve(f);
        }

        ImGui.SameLine();
        if (ImGui.Button("Skip (keep current)")) engine.Resolve(null);

        ImGui.SetNextItemWidth(120);
        ImGui.InputInt("##custom", ref customPrice, 0);
        ImGui.SameLine();
        if (ImGui.Button("Set custom price") && customPrice > 0) engine.Resolve(customPrice);

        if (ImGui.Button("Accept and lower floor to this price"))
        {
            p.Rule.MinPrice = d.ProposedPrice;
            config.Save();
            engine.Resolve(d.ProposedPrice);
        }

        var left = (p.Deadline - DateTime.Now).TotalSeconds;
        ImGui.TextDisabled($"Auto-{config.TimeoutFallback} in {Math.Max(0, left):0}s");
    }
}
