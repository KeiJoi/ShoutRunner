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
    private int editIndex = -1;
    private MacroActionType editActionType = MacroActionType.Shout;
    private string editPayload = string.Empty;
    private int editTeleportIndex;
    private int editWorldIndex;
    private string editTeleportFilter = string.Empty;
    private string presetName = string.Empty;
    private int presetIndex;
    private int serverDragIndex = -1;
    private int teleportDragIndex = -1;

    private static readonly string[] TeleportFallback =
    {
        "Ul'dah - Steps of Nald",
        "Limsa Lominsa",
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
        ("Cuchulainn", "Dynamis"),
        ("Golem", "Dynamis"),
        ("Kraken", "Dynamis"),
        ("Maduin", "Dynamis"),
        ("Marilith", "Dynamis"),
        ("Rafflesia", "Dynamis"),
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

        if (ImGui.BeginTabBar("##shoutrunner-tabs"))
        {
            if (ImGui.BeginTabItem("Advanced Macro"))
            {
                DrawInterval();
                ImGui.Separator();
                DrawActions();
                ImGui.Separator();
                DrawPresets();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Simple Macro"))
            {
                DrawSimpleMacro();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

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
        if (ImGui.BeginTable("##actions", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 120);
            ImGui.TableSetupColumn("Details");
            ImGui.TableSetupColumn("Order", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, 120);
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
                ImGui.BeginDisabled(i == 0);
                if (ImGui.Button($"Up##{i}"))
                {
                    MoveAction(i, i - 1);
                    ImGui.EndTable();
                    return;
                }
                ImGui.EndDisabled();
                ImGui.SameLine();
                ImGui.BeginDisabled(i == configuration.Actions.Count - 1);
                if (ImGui.Button($"Down##{i}"))
                {
                    MoveAction(i, i + 1);
                    ImGui.EndTable();
                    return;
                }
                ImGui.EndDisabled();
                ImGui.TableSetColumnIndex(3);
                if (ImGui.Button($"Edit##{i}"))
                {
                    BeginEditAction(i);
                }
                ImGui.SameLine();
                if (ImGui.Button($"Remove##{i}"))
                {
                    configuration.Actions.RemoveAt(i);
                    configuration.Save();
                    if (editIndex == i)
                        editIndex = -1;
                    else if (editIndex > i)
                        editIndex--;
                    ImGui.EndTable();
                    return;
                }
            }

            ImGui.EndTable();
        }

        ImGui.Separator();
        ImGui.Text("Add action");
        DrawActionEditor("Type", ref newActionType, ref newPayload, ref teleportIndex, ref worldIndex, ref teleportFilter, allowCustomWorld: false);
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
        ImGui.TextDisabled("Shout uses /shout, teleport uses native teleport, world/DC uses Lifestream if installed.");

        if (editIndex >= 0 && editIndex < configuration.Actions.Count)
        {
            ImGui.Separator();
            ImGui.Text($"Edit action #{editIndex + 1}");
            DrawActionEditor("Type##edit", ref editActionType, ref editPayload, ref editTeleportIndex, ref editWorldIndex, ref editTeleportFilter, allowCustomWorld: true);
            if (ImGui.Button("Save changes"))
            {
                var updated = new MacroAction
                {
                    Type = editActionType,
                    Payload = editPayload
                };
                configuration.Actions[editIndex] = updated;
                configuration.Save();
                editIndex = -1;
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
                editIndex = -1;
        }
    }

    private void DrawSimpleMacro()
    {
        EnsureSimpleDefaults();

        ImGui.Text("Shout message");
        ImGui.SetNextItemWidth(420);
        var simpleShout = configuration.SimpleShout;
        if (ImGui.InputText("##simple-shout", ref simpleShout, 256))
        {
            configuration.SimpleShout = simpleShout;
            configuration.Save();
        }

        ImGui.Separator();
        ImGui.Text("Order");
        if (ImGui.BeginTable("##simple-order", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.TableSetupColumn("Servers", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Teleports", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableHeadersRow();

            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            DrawReorderableList("Servers", configuration.SimpleServerOrder, ref serverDragIndex);
            ImGui.TableSetColumnIndex(1);
            DrawReorderableList("Teleports", configuration.SimpleTeleportOrder, ref teleportDragIndex);

            ImGui.EndTable();
        }

        ImGui.Separator();
        ImGui.TextDisabled("Sequence: server -> teleport -> shout -> teleport -> shout -> teleport -> shout -> next server.");
        ImGui.BeginDisabled(string.IsNullOrWhiteSpace(configuration.SimpleShout));
        if (ImGui.Button("Apply To Macro"))
        {
            ApplySimpleMacro();
        }
        ImGui.EndDisabled();
    }

    private void EnsureSimpleDefaults()
    {
        if (configuration.SimpleServerOrder.Count == 0)
        {
            configuration.SimpleServerOrder = NorthAmericaWorlds.Select(w => w.World).ToList();
        }

        if (configuration.SimpleTeleportOrder.Count == 0)
        {
            configuration.SimpleTeleportOrder = new List<string>
            {
                "New Gridania",
                "Ul'dah - Steps of Nald",
                "Limsa Lominsa Lower Decks"
            };
        }
    }

    private void ApplySimpleMacro()
    {
        macroRunner.Stop();
        configuration.Actions.Clear();

        foreach (var server in configuration.SimpleServerOrder)
        {
            configuration.Actions.Add(new MacroAction
            {
                Type = MacroActionType.WorldVisit,
                Payload = server
            });

            foreach (var teleport in configuration.SimpleTeleportOrder)
            {
                configuration.Actions.Add(new MacroAction
                {
                    Type = MacroActionType.Teleport,
                    Payload = teleport
                });

                configuration.Actions.Add(new MacroAction
                {
                    Type = MacroActionType.Shout,
                    Payload = configuration.SimpleShout
                });
            }
        }

        configuration.Save();
    }

    private void DrawReorderableList(string label, List<string> items, ref int dragIndex)
    {
        var height = Math.Max(120, items.Count * 22);
        ImGui.BeginChild($"{label}##list", new Vector2(0, height), true);

        for (var i = 0; i < items.Count; i++)
        {
            ImGui.Selectable(items[i], false);

            if (ImGui.IsItemActive() && ImGui.IsMouseDragging(0))
            {
                dragIndex = i;
            }

            if (dragIndex >= 0 && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem))
            {
                if (dragIndex != i)
                {
                    MoveListItem(items, dragIndex, i);
                    dragIndex = i;
                    configuration.Save();
                }
            }
        }

        if (!ImGui.IsMouseDragging(0))
            dragIndex = -1;

        ImGui.EndChild();
    }

    private static void MoveListItem(List<string> items, int from, int to)
    {
        if (from == to || from < 0 || from >= items.Count || to < 0 || to >= items.Count)
            return;

        var item = items[from];
        items.RemoveAt(from);
        items.Insert(to, item);
    }

    private void DrawActionEditor(string typeLabel, ref MacroActionType actionType, ref string payload, ref int teleportSelection, ref int worldSelection, ref string filter, bool allowCustomWorld)
    {
        if (ImGui.BeginCombo(typeLabel, actionType.ToString()))
        {
            foreach (MacroActionType type in Enum.GetValues(typeof(MacroActionType)))
            {
                if (type == MacroActionType.DataCenterVisit)
                    continue; // Data center handled automatically during world visits.

                if (ImGui.Selectable(type.ToString(), type == actionType))
                {
                    actionType = type;
                }
            }
            ImGui.EndCombo();
        }

        if (actionType == MacroActionType.Teleport)
        {
            EnsureTeleportDestinations();
            var list = GetTeleportListWithPayload(payload);
            teleportSelection = ClampSelection(teleportSelection, list.Count);
            var payloadValue = payload;
            var matchIndex = -1;
            for (var i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i], payloadValue, StringComparison.OrdinalIgnoreCase))
                {
                    matchIndex = i;
                    break;
                }
            }
            if (matchIndex >= 0)
                teleportSelection = matchIndex;

            ImGui.SetNextItemWidth(240);
            ImGui.InputText("Filter", ref filter, 64);

            var preview = list[teleportSelection];
            if (ImGui.BeginCombo("Aetheryte", preview))
            {
                for (var i = 0; i < list.Count; i++)
                {
                    var name = list[i];
                    if (!string.IsNullOrEmpty(filter) &&
                        !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (ImGui.Selectable(name, i == teleportSelection))
                        teleportSelection = i;
                }
                ImGui.EndCombo();
            }
            payload = list[teleportSelection];
        }
        else if (actionType == MacroActionType.WorldVisit)
        {
            var payloadValue = payload;
            var matchIndex = -1;
            for (var i = 0; i < NorthAmericaWorlds.Length; i++)
            {
                if (string.Equals(NorthAmericaWorlds[i].World, payloadValue, StringComparison.OrdinalIgnoreCase))
                {
                    matchIndex = i;
                    break;
                }
            }
            if (matchIndex >= 0)
                worldSelection = matchIndex;

            worldSelection = ClampSelection(worldSelection, NorthAmericaWorlds.Length);
            var currentLabel = $"{NorthAmericaWorlds[worldSelection].World} ({NorthAmericaWorlds[worldSelection].Dc})";
            if (ImGui.BeginCombo("World", currentLabel))
            {
                for (var i = 0; i < NorthAmericaWorlds.Length; i++)
                {
                    var label = $"{NorthAmericaWorlds[i].World} ({NorthAmericaWorlds[i].Dc})";
                    if (ImGui.Selectable(label, i == worldSelection))
                        worldSelection = i;
                }
                ImGui.EndCombo();
            }
            payload = NorthAmericaWorlds[worldSelection].World;

            if (allowCustomWorld)
            {
                ImGui.SetNextItemWidth(240);
                ImGui.InputText("Custom world", ref payload, 64);
            }
        }
        else
        {
            ImGui.SetNextItemWidth(320);
            ImGui.InputText("Message", ref payload, 256);
        }
    }

    private static int ClampSelection(int index, int count)
    {
        if (count <= 0)
            return 0;
        if (index < 0 || index >= count)
            return 0;
        return index;
    }

    private List<string> GetTeleportListWithPayload(string payload)
    {
        var list = teleportDestinations.Count > 0 ? teleportDestinations : TeleportFallback.ToList();
        if (!string.IsNullOrWhiteSpace(payload) &&
            !list.Any(name => string.Equals(name, payload, StringComparison.OrdinalIgnoreCase)))
        {
            list = new List<string>(list);
            list.Insert(0, payload.Trim());
        }

        return list;
    }

    private void BeginEditAction(int index)
    {
        if (index < 0 || index >= configuration.Actions.Count)
            return;

        var action = configuration.Actions[index];
        editIndex = index;
        editActionType = action.Type;
        editPayload = action.Payload;
        editTeleportIndex = 0;
        editWorldIndex = 0;
        editTeleportFilter = string.Empty;

        if (editActionType == MacroActionType.Teleport)
        {
            EnsureTeleportDestinations();
            var list = GetTeleportListWithPayload(editPayload);
            var matchIndex = list.FindIndex(name => string.Equals(name, editPayload, StringComparison.OrdinalIgnoreCase));
            if (matchIndex >= 0)
                editTeleportIndex = matchIndex;
        }
        else if (editActionType == MacroActionType.WorldVisit)
        {
            var matchIndex = Array.FindIndex(NorthAmericaWorlds, w => string.Equals(w.World, editPayload, StringComparison.OrdinalIgnoreCase));
            if (matchIndex >= 0)
                editWorldIndex = matchIndex;
        }
    }

    private void MoveAction(int from, int to)
    {
        if (from < 0 || from >= configuration.Actions.Count || to < 0 || to >= configuration.Actions.Count)
            return;

        var item = configuration.Actions[from];
        configuration.Actions.RemoveAt(from);
        configuration.Actions.Insert(to, item);
        configuration.Save();

        if (editIndex == from)
            editIndex = to;
        else if (editIndex >= 0)
        {
            if (from < editIndex && to >= editIndex)
                editIndex--;
            else if (from > editIndex && to <= editIndex)
                editIndex++;
        }
    }

    private void DrawPresets()
    {
        ImGui.Text("Macros");
        ImGui.SetNextItemWidth(220);
        ImGui.InputText("Preset name", ref presetName, 64);

        ImGui.BeginDisabled(string.IsNullOrWhiteSpace(presetName));
        if (ImGui.Button("Save current"))
        {
            SavePreset(presetName);
        }
        ImGui.EndDisabled();

        if (configuration.Presets.Count == 0)
        {
            ImGui.TextDisabled("No saved macros yet.");
            return;
        }

        presetIndex = ClampSelection(presetIndex, configuration.Presets.Count);
        var currentName = configuration.Presets[presetIndex].Name;
        if (ImGui.BeginCombo("Saved macros", currentName))
        {
            for (var i = 0; i < configuration.Presets.Count; i++)
            {
                var name = configuration.Presets[i].Name;
                if (ImGui.Selectable(name, i == presetIndex))
                {
                    presetIndex = i;
                    presetName = name;
                }
            }
            ImGui.EndCombo();
        }

        if (ImGui.Button("Load"))
        {
            LoadPreset(configuration.Presets[presetIndex]);
        }
        ImGui.SameLine();
        if (ImGui.Button("Delete"))
        {
            configuration.Presets.RemoveAt(presetIndex);
            configuration.Save();
            presetIndex = 0;
        }
    }

    private void SavePreset(string name)
    {
        var trimmed = name.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return;

        var existing = configuration.Presets.FirstOrDefault(p => string.Equals(p.Name, trimmed, StringComparison.OrdinalIgnoreCase));
        var actions = configuration.Actions.Select(action => action.Clone()).ToList();

        if (existing == null)
        {
            configuration.Presets.Add(new MacroPreset
            {
                Name = trimmed,
                Actions = actions
            });
            presetIndex = configuration.Presets.Count - 1;
        }
        else
        {
            existing.Name = trimmed;
            existing.Actions = actions;
            presetIndex = configuration.Presets.IndexOf(existing);
        }

        configuration.Save();
    }

    private void LoadPreset(MacroPreset preset)
    {
        macroRunner.Stop();
        configuration.Actions = preset.Actions.Select(action => action.Clone()).ToList();
        configuration.Save();
        editIndex = -1;
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
