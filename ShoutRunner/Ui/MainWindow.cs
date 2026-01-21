using System;
using System.Numerics;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;

namespace ShoutRunner.Ui;

public sealed class MainWindow : Window
{
    private readonly Configuration configuration;
    private readonly MacroRunner macroRunner;

    private MacroActionType newActionType = MacroActionType.Shout;
    private string newPayload = string.Empty;
    private int teleportIndex;
    private int worldIndex;

    private static readonly string[] TeleportDestinations =
    {
        "Ul'dah - Steps of Nald",
        "Limsa Lominsa Lower Decks",
        "New Gridania",
        "Foundation"
    };

    private static readonly (string World, string Dc)[] NorthAmericaWorlds =
    {
        ("Adamantoise", "Aether"),
        ("Cactuar", "Aether"),
        ("Faerie", "Aether"),
        ("Gilgamesh", "Aether"),
        ("Jenova", "Aether"),
        ("Midgardsormr", "Aether"),
        ("Sargatanas", "Aether"),
        ("Siren", "Aether"),
        ("Behemoth", "Primal"),
        ("Excalibur", "Primal"),
        ("Exodus", "Primal"),
        ("Famfrit", "Primal"),
        ("Hyperion", "Primal"),
        ("Lamia", "Primal"),
        ("Leviathan", "Primal"),
        ("Ultros", "Primal"),
        ("Balmung", "Crystal"),
        ("Brynhildr", "Crystal"),
        ("Coeurl", "Crystal"),
        ("Diabolos", "Crystal"),
        ("Goblin", "Crystal"),
        ("Malboro", "Crystal"),
        ("Mateus", "Crystal"),
        ("Zalera", "Crystal"),
        ("Halicarnassus", "Dynamis"),
        ("Maduin", "Dynamis"),
        ("Marilith", "Dynamis"),
        ("Seraph", "Dynamis"),
    };

    public MainWindow(Configuration configuration, MacroRunner macroRunner)
        : base("ShoutRunner")
    {
        this.configuration = configuration;
        this.macroRunner = macroRunner;

        Size = new Vector2(520, 420);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        DrawStatus();
        ImGui.Separator();
        DrawInterval();
        ImGui.Separator();
        DrawActions();
    }

    private void DrawStatus()
    {
        if (macroRunner.Running)
        {
            ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), "Macro is running");
            var next = macroRunner.NextRun?.ToLocalTime();
            if (next != null)
                ImGui.SameLine();
            if (next != null)
                ImGui.Text($"Next run: {next:HH:mm:ss}");

            if (!string.IsNullOrEmpty(macroRunner.LastError))
            {
                ImGui.TextColored(new Vector4(1f, 0.6f, 0.4f, 1f), $"Last error: {macroRunner.LastError}");
            }
        }
        else
        {
            ImGui.TextColored(new Vector4(1f, 0.9f, 0.4f, 1f), "Macro is stopped");
        }

        if (ImGui.Button(macroRunner.Running ? "Stop" : "Start"))
        {
            if (macroRunner.Running)
            {
                macroRunner.Stop();
            }
            else
            {
                macroRunner.Start();
            }
        }
        ImGui.SameLine();
        ImGui.TextDisabled("Use /shoutrunner to open this window");
    }

    private void DrawInterval()
    {
        var hours = configuration.IntervalHours;
        var minutes = configuration.IntervalMinutes;
        var seconds = configuration.IntervalSeconds;
        var repeat = configuration.RepeatEnabled;
        var delay = configuration.DelayBetweenActionsSeconds;

        ImGui.Text("Interval (hh:mm:ss)");
        ImGui.SetNextItemWidth(80);
        if (ImGui.InputInt("Hours", ref hours, 1, 3))
        {
            configuration.IntervalHours = Math.Max(0, hours);
            configuration.Save();
        }
        ImGui.SetNextItemWidth(80);
        if (ImGui.InputInt("Minutes", ref minutes, 1, 5))
        {
            configuration.IntervalMinutes = Math.Max(0, minutes);
            configuration.Save();
        }
        ImGui.SetNextItemWidth(80);
        if (ImGui.InputInt("Seconds", ref seconds, 1, 5))
        {
            configuration.IntervalSeconds = Math.Max(0, seconds);
            configuration.Save();
        }

        if (ImGui.Checkbox("Repeat macro after it finishes", ref repeat))
        {
            configuration.RepeatEnabled = repeat;
            configuration.Save();
        }

        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("Delay between actions (seconds)", ref delay, 1, 5))
        {
            configuration.DelayBetweenActionsSeconds = delay;
            configuration.ClampDelaySeconds();
            configuration.Save();
        }
    }

    private void DrawActions()
    {
        ImGui.Text("Macro actions (executed in order)");
        if (ImGui.BeginTable("##actions", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 120);
            ImGui.TableSetupColumn("Details");
            ImGui.TableSetupColumn("##remove", ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableHeadersRow();

            for (var i = 0; i < configuration.Actions.Count; i++)
            {
                var action = configuration.Actions[i];
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.Text(action.FriendlyName);
                ImGui.TableSetColumnIndex(1);
                ImGui.TextWrapped(string.IsNullOrEmpty(action.Payload) ? "(not configured)" : action.DisplayDetail);
                ImGui.TableSetColumnIndex(2);
                if (ImGui.Button($"Remove##{i}"))
                {
                    configuration.Actions.RemoveAt(i);
                    configuration.Save();
                    ImGui.EndTable();
                    return;
                }
            }

            ImGui.EndTable();
        }

        ImGui.Separator();
        ImGui.Text("Add action");

        if (ImGui.BeginCombo("Type", newActionType.ToString()))
        {
            foreach (MacroActionType type in Enum.GetValues(typeof(MacroActionType)))
            {
                if (type == MacroActionType.DataCenterVisit)
                    continue; // Data center handled automatically during world visits.

                if (ImGui.Selectable(type.ToString(), type == newActionType))
                {
                    newActionType = type;
                }
            }
            ImGui.EndCombo();
        }

        if (newActionType == MacroActionType.Teleport)
        {
            if (ImGui.BeginCombo("Aetheryte", TeleportDestinations[teleportIndex]))
            {
                for (var i = 0; i < TeleportDestinations.Length; i++)
                {
                    if (ImGui.Selectable(TeleportDestinations[i], i == teleportIndex))
                        teleportIndex = i;
                }
                ImGui.EndCombo();
            }
            newPayload = TeleportDestinations[teleportIndex];
        }
        else if (newActionType == MacroActionType.WorldVisit)
        {
            var currentLabel = $"{NorthAmericaWorlds[worldIndex].World} ({NorthAmericaWorlds[worldIndex].Dc})";
            if (ImGui.BeginCombo("World", currentLabel))
            {
                for (var i = 0; i < NorthAmericaWorlds.Length; i++)
                {
                    var label = $"{NorthAmericaWorlds[i].World} ({NorthAmericaWorlds[i].Dc})";
                    if (ImGui.Selectable(label, i == worldIndex))
                        worldIndex = i;
                }
                ImGui.EndCombo();
            }
            newPayload = NorthAmericaWorlds[worldIndex].World;
        }
        else
        {
            ImGui.SetNextItemWidth(320);
            ImGui.InputText("Message", ref newPayload, 256);
        }
        ImGui.SameLine();
        if (ImGui.Button("Add"))
        {
            var action = new MacroAction
            {
                Type = newActionType,
                Payload = newPayload
            };
            configuration.Actions.Add(action);
            configuration.Save();
            newPayload = string.Empty;
        }
        ImGui.TextDisabled("Shout uses /shout, teleport uses /tele, world uses /visit (auto datacenter if needed).");
    }
}
