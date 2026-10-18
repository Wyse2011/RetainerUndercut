using AutoRetainerAPI;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using RetainerUndercut.Windows;

namespace RetainerUndercut;

public sealed unsafe class Plugin : IDalamudPlugin
{
    private const string Command = "/undercut";

    private readonly Configuration config;
    private readonly MarketWatcher market;
    private readonly UndercutEngine engine;
    private readonly WindowSystem windows = new("RetainerUndercut");
    private readonly ConfigWindow configWindow;
    private readonly AutoRetainerApi autoRetainer;

    public Plugin(IDalamudPluginInterface pi, IMarketBoard marketBoard)
    {
        ECommonsMain.Init(pi, this);

        config = pi.GetPluginConfig() as Configuration ?? new Configuration();
        market = new MarketWatcher(marketBoard, config);
        engine = new UndercutEngine(config, market);

        configWindow = new ConfigWindow(config, engine);
        windows.AddWindow(configWindow);
        windows.AddWindow(new ConfirmWindow(engine, config));
        pi.UiBuilder.Draw += windows.Draw;
        pi.UiBuilder.OpenConfigUi += () => configWindow.IsOpen = true;
        pi.UiBuilder.OpenMainUi += () => configWindow.IsOpen = true;

        Svc.Commands.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open settings. '/undercut run' processes the open retainer (or all, from the retainer list). '/undercut stop' aborts.",
        });

        // ── AutoRetainer integration ──
        // 1) For each retainer AutoRetainer visits, it asks "anyone want to postprocess this one?"
        // 2) We say yes if it has listings.
        // 3) AutoRetainer hands us the UI at the retainer's menu; we do our thing and hand it back.
        autoRetainer = new AutoRetainerApi();
        autoRetainer.OnRetainerPostprocessStep += name =>
        {
            if (config.Enabled && config.IntegrateWithAutoRetainer
                && !config.ExcludedRetainers.Contains(name)
                && UndercutEngine.RetainerHasListings(name))
                autoRetainer.RequestRetainerPostprocess();
        };
        autoRetainer.OnRetainerReadyToPostprocess += name =>
            engine.ProcessCurrentRetainer(name, whenDone: autoRetainer.FinishRetainerPostProcess);
    }

    private void OnCommand(string cmd, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "run":
                if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerList", out var rl) && GenericHelpers.IsAddonReady(rl))
                {
                    engine.ProcessAllRetainers();
                }
                else
                {
                    var active = RetainerManager.Instance()->GetActiveRetainer();
                    if (active == null) { Svc.Chat.Print("[Undercut] Open a retainer or the retainer list first."); return; }
                    engine.ProcessCurrentRetainer(active->NameString);
                }
                break;
            case "stop":
                engine.Abort();
                break;
            default:
                configWindow.Toggle();
                break;
        }
    }

    public void Dispose()
    {
        Svc.Commands.RemoveHandler(Command);
        windows.RemoveAllWindows();
        autoRetainer.Dispose();
        engine.Dispose();
        market.Dispose();
        ECommonsMain.Dispose();
    }
}
