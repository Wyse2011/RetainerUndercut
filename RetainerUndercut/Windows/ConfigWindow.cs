using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using System;
using System.Linq;
using System.Numerics;

namespace RetainerUndercut.Windows;

public class ConfigWindow : Window
{
    private readonly Configuration C;
    private readonly UndercutEngine engine;
    private string ruleFilter = "";
    private string newExcluded = "";

    public ConfigWindow(Configuration config, UndercutEngine engine) : base("Retainer Undercut###RUConfig")
    {
        C = config;
        this.engine = engine;
        SizeConstraints = new() { MinimumSize = new(520, 380), MaximumSize = new(4000, 4000) };
    }

    public override void Draw()
    {
        if (engine.IsBusy)
        {
            ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), "Running…");
            ImGui.SameLine();
            if (ImGui.Button("Stop")) engine.Abort();
        }

        using var tabs = ImRaii.TabBar("tabs");
        if (!tabs) return;
        DrawTab("Settings", DrawSettings);
        DrawTab($"Item floors ({C.Rules.Count})", DrawRules);
        DrawTab($"Pending review ({engine.Review.Count})", DrawReview);
        DrawTab("Log", DrawLog);
    }

    private static void DrawTab(string label, Action draw)
    {
        using var t = ImRaii.TabItem(label);
        if (t) draw();
    }

    private void DrawSettings()
    {
        var changed = false;
        changed |= ImGui.Checkbox("Enabled", ref C.Enabled);
        changed |= ImGui.Checkbox("Run as AutoRetainer postprocess task", ref C.IntegrateWithAutoRetainer);

        ImGui.SetNextItemWidth(120);
        changed |= ImGui.InputInt("Undercut by (gil)", ref C.UndercutAmount);
        C.UndercutAmount = Math.Max(0, C.UndercutAmount);

        ImGui.SetNextItemWidth(120);
        changed |= ImGui.SliderFloat("Confirm drops larger than %", ref C.MaxDropPercent, 0, 100, "%.0f%%");
        ImGui.TextDisabled("0% = never ask based on drop size (floors still apply).");

        changed |= ImGui.Checkbox("HQ items only compete with HQ listings", ref C.HqOnlyComparesHq);
        changed |= ImGui.Checkbox("Raise price when I'm already cheapest", ref C.RaiseWhenCheapest);

        ImGui.Separator();
        changed |= EnumCombo("When confirmation is needed", ref C.ConfirmBehavior);
        if (C.ConfirmBehavior == ConfirmBehavior.Ask)
        {
            ImGui.SetNextItemWidth(120);
            changed |= ImGui.InputInt("Wait this many seconds", ref C.ConfirmTimeoutSeconds);
            changed |= EnumCombo("Then", ref C.TimeoutFallback);
        }

        ImGui.Separator();
        ImGui.SetNextItemWidth(120);
        changed |= ImGui.InputInt("Delay between items (ms)", ref C.DelayBetweenItemsMs);
        C.DelayBetweenItemsMs = Math.Max(1000, C.DelayBetweenItemsMs);
        ImGui.SetNextItemWidth(350);
        changed |= ImGui.InputText("Sell menu text", ref C.SellMenuText, 200);
        ImGui.SetNextItemWidth(120);
        changed |= ImGui.InputInt("Price input index", ref C.PriceInputIndex);

        ImGui.Separator();
        ImGui.TextUnformatted("Excluded retainers");
        foreach (var r in C.ExcludedRetainers.ToArray())
        {
            if (ImGui.SmallButton($"x##{r}")) { C.ExcludedRetainers.Remove(r); changed = true; }
            ImGui.SameLine();
            ImGui.TextUnformatted(r);
        }
        ImGui.SetNextItemWidth(200);
        ImGui.InputText("##excl", ref newExcluded, 50);
        ImGui.SameLine();
        if (ImGui.Button("Add") && newExcluded.Length > 0) { C.ExcludedRetainers.Add(newExcluded); newExcluded = ""; changed = true; }

        if (changed) C.Save();
    }

    private void DrawRules()
    {
        ImGui.TextDisabled("Items appear here after they're checked once. Floor 0 = no floor.");
        ImGui.SetNextItemWidth(250);
        ImGui.InputTextWithHint("##filter", "Filter…", ref ruleFilter, 100);

        using var table = ImRaii.Table("rules", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable);
        if (!table) return;
        ImGui.TableSetupColumn("On", ImGuiTableColumnFlags.WidthFixed, 30);
        ImGui.TableSetupColumn("Item");
        ImGui.TableSetupColumn("Floor", ImGuiTableColumnFlags.WidthFixed, 110);
        ImGui.TableSetupColumn("Last set", ImGuiTableColumnFlags.WidthFixed, 90);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 30);
        ImGui.TableHeadersRow();

        string? remove = null;
        foreach (var (key, rule) in C.Rules.OrderBy(x => x.Value.Name))
        {
            if (ruleFilter.Length > 0 && !rule.Name.Contains(ruleFilter, StringComparison.OrdinalIgnoreCase)) continue;
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            if (ImGui.Checkbox($"##en{key}", ref rule.Enabled)) C.Save();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(rule.Name);
            ImGui.TableNextColumn();
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputInt($"##min{key}", ref rule.MinPrice, 0)) { rule.MinPrice = Math.Max(0, rule.MinPrice); C.Save(); }
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(rule.LastSetPrice?.ToString("N0") ?? "-");
            ImGui.TableNextColumn();
            if (ImGui.SmallButton($"x##del{key}")) remove = key;
        }
        if (remove != null) { C.Rules.Remove(remove); C.Save(); }
    }

    private void DrawReview()
    {
        ImGui.TextDisabled("Listings left alone because the new price tripped a floor or the drop limit.");
        if (ImGui.Button("Clear")) engine.Review.Clear();
        foreach (var r in engine.Review.AsEnumerable().Reverse())
            ImGui.TextWrapped($"[{r.Time:MM/dd HH:mm}] {r.Retainer} / {r.ItemName}: {r.Current:N0} → {r.Proposed:N0} (lowest {r.Lowest:N0}). {r.Reason}");
    }

    private void DrawLog()
    {
        using var child = ImRaii.Child("log");
        foreach (var line in engine.Log) ImGui.TextUnformatted(line);
        if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY()) ImGui.SetScrollHereY(1f);
    }

    private static bool EnumCombo<T>(string label, ref T value) where T : struct, Enum
    {
        var changed = false;
        ImGui.SetNextItemWidth(160);
        using var combo = ImRaii.Combo(label, value.ToString());
        if (!combo) return false;
        foreach (var v in Enum.GetValues<T>())
            if (ImGui.Selectable(v.ToString(), v.Equals(value))) { value = v; changed = true; }
        return changed;
    }
}
