using System;
using System.Linq;
using System.Numerics;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using Dalamud.Bindings.ImGui;

namespace ShoutRunner.Ui;

public sealed class MainWindow : Window
{
    private readonly Configuration configuration;
    private readonly MacroRunner macroRunner;
    private readonly IDataManager dataManager;

    private MacroActionType newActionType = MacroActionType.Shout;
    private string newPayload = string.Empty;
    private int teleportIndex;
    private int worldIndex;
    private string teleportFilter = string.Empty;
    private bool teleportLoaded;

    private static readonly string[] TeleportFallback =
    {
        "Ul'dah - Steps of Nald",
        "Limsa Lominsa Lower Decks",
        "New Gridania",
        "Foundation"
    };

    private readonly List<string> teleportDestinations = new();

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

    public MainWindow(Configuration configuration, MacroRunner macroRunner, IDataManager dataManager)
        : base("ShoutRunner")
    {
        this.configuration = configuration;
        this.macroRunner = macroRunner;
        this.dataManager = dataManager;

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
        DrawProgress();
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
            EnsureTeleportDestinations();
            var list = teleportDestinations.Count > 0 ? teleportDestinations : TeleportFallback.ToList();
            if (teleportIndex >= list.Count)
                teleportIndex = 0;

            ImGui.SetNextItemWidth(240);
            ImGui.InputText("Filter", ref teleportFilter, 64);

            var preview = list[teleportIndex];
            if (ImGui.BeginCombo("Aetheryte", preview))
            {
                for (var i = 0; i < list.Count; i++)
                {
                    var name = list[i];
                    if (!string.IsNullOrEmpty(teleportFilter) &&
                        !name.Contains(teleportFilter, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (ImGui.Selectable(name, i == teleportIndex))
                        teleportIndex = i;
                }
                ImGui.EndCombo();
            }
            newPayload = list[teleportIndex];
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
        ImGui.TextDisabled("Shout uses /shout, teleport uses /teleport, world uses /visit (auto datacenter if needed).");
    }

    private void EnsureTeleportDestinations()
    {
        if (teleportLoaded)
            return;

        teleportLoaded = true;
        try
        {
            var sheet = dataManager.GetExcelSheet<Aetheryte>();
            if (sheet == null)
                return;

            var rowType = typeof(Aetheryte);
            var isAetheryteProperty = rowType.GetProperty("IsAetheryte");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var row in sheet)
            {
                if (isAetheryteProperty?.PropertyType == typeof(bool))
                {
                    if (!(bool)(isAetheryteProperty.GetValue(row) ?? false))
                        continue;
                }

                if (row.PlaceName.Value.Name.IsEmpty)
                    continue;

                var name = row.PlaceName.Value.Name.ToString();
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                if (seen.Add(name))
                    teleportDestinations.Add(name);
            }

            teleportDestinations.Sort(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            teleportDestinations.Clear();
        }
    }

    private void DrawProgress()
    {
        if (!macroRunner.TryGetProgress(out var value, out var label))
            return;

        ImGui.Separator();
        ImGui.Text(label);
        ImGui.ProgressBar(value, new Vector2(-1, 0));
    }
}
