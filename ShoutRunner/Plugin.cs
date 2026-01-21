using System;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Game.Command;
using ECommons;
using ShoutRunner.Ui;

namespace ShoutRunner;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/shoutrunner";
    private const string ShoutTestCommand = "/shouttest";

    public string Name => "ShoutRunner";

    private readonly WindowSystem windowSystem = new("ShoutRunner");
    private readonly Configuration configuration;
    private readonly MacroRunner macroRunner;
    private readonly MainWindow mainWindow;

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;

    public Plugin()
    {
        ECommonsMain.Init(PluginInterface, this);
        configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        configuration.Initialize(PluginInterface);

        macroRunner = new MacroRunner(configuration, CommandManager, ChatGui, Condition, ClientState, ObjectTable, Framework);
        mainWindow = new MainWindow(configuration, macroRunner, DataManager);

        windowSystem.AddWindow(mainWindow);
        PluginInterface.UiBuilder.Draw += DrawUi;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleMainWindow;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainWindow;

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Opens the ShoutRunner window."
        });
        CommandManager.AddHandler(ShoutTestCommand, new CommandInfo(OnShoutTestCommand)
        {
            HelpMessage = "Send a one-off /shout test from ShoutRunner."
        });

        Framework.Update += OnFrameworkUpdate;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        macroRunner.Tick();
    }

    private void DrawUi()
    {
        windowSystem.Draw();
    }

    private void ToggleMainWindow()
    {
        mainWindow.IsOpen = !mainWindow.IsOpen;
    }

    private void OnCommand(string command, string args)
    {
        ToggleMainWindow();
    }

    public void Dispose()
    {
        windowSystem.RemoveAllWindows();
        Framework.Update -= OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw -= DrawUi;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleMainWindow;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainWindow;
        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(ShoutTestCommand);

        configuration.Save();
        macroRunner.Dispose();
        ECommonsMain.Dispose();
    }

    private void OnShoutTestCommand(string command, string args)
    {
        macroRunner.RunSingleShoutTest(string.IsNullOrWhiteSpace(args) ? "ShoutRunner test" : args.Trim());
    }
}
