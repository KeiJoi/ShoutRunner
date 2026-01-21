using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using ECommons.Automation;

namespace ShoutRunner;

public sealed class MacroRunner : IDisposable
{
    private readonly Configuration config;
    private readonly ICommandManager commandManager;
    private readonly IChatGui chatGui;
    private readonly ICondition condition;
    private readonly IClientState clientState;
    private readonly IFramework framework;

    private readonly Dictionary<string, string> worldToDataCenter;
    private readonly List<string> worldVisitOrder;
    private readonly Queue<QueuedCommand> commandQueue = new();
    private readonly object commandLock = new();

    private CancellationTokenSource? executionCts;
    private bool executing;

    public bool Running { get; private set; }

    public DateTime? NextRun { get; private set; }

    public string LastError { get; private set; } = string.Empty;

    public MacroRunner(Configuration config, ICommandManager commandManager, IChatGui chatGui, ICondition condition, IClientState clientState, IFramework framework)
    {
        this.config = config;
        this.commandManager = commandManager;
        this.chatGui = chatGui;
        this.condition = condition;
        this.clientState = clientState;
        this.framework = framework;

        worldToDataCenter = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Aether
            ["Adamantoise"] = "Aether",
            ["Cactuar"] = "Aether",
            ["Faerie"] = "Aether",
            ["Gilgamesh"] = "Aether",
            ["Jenova"] = "Aether",
            ["Midgardsormr"] = "Aether",
            ["Sargatanas"] = "Aether",
            ["Siren"] = "Aether",
            // Primal
            ["Behemoth"] = "Primal",
            ["Excalibur"] = "Primal",
            ["Exodus"] = "Primal",
            ["Famfrit"] = "Primal",
            ["Hyperion"] = "Primal",
            ["Lamia"] = "Primal",
            ["Leviathan"] = "Primal",
            ["Ultros"] = "Primal",
            // Crystal
            ["Balmung"] = "Crystal",
            ["Brynhildr"] = "Crystal",
            ["Coeurl"] = "Crystal",
            ["Diabolos"] = "Crystal",
            ["Goblin"] = "Crystal",
            ["Malboro"] = "Crystal",
            ["Mateus"] = "Crystal",
            ["Zalera"] = "Crystal",
            // Dynamis
            ["Halicarnassus"] = "Dynamis",
            ["Maduin"] = "Dynamis",
            ["Marilith"] = "Dynamis",
            ["Seraph"] = "Dynamis",
        };

        worldVisitOrder = new List<string>
        {
            "Adamantoise",
            "Cactuar",
            "Faerie",
            "Gilgamesh",
            "Jenova",
            "Midgardsormr",
            "Sargatanas",
            "Siren",
            "Behemoth",
            "Excalibur",
            "Exodus",
            "Famfrit",
            "Hyperion",
            "Lamia",
            "Leviathan",
            "Ultros",
            "Balmung",
            "Brynhildr",
            "Coeurl",
            "Diabolos",
            "Goblin",
            "Malboro",
            "Mateus",
            "Zalera",
            "Halicarnassus",
            "Maduin",
            "Marilith",
            "Seraph"
        };
    }

    public void Start()
    {
        Running = true;
        NextRun = DateTime.UtcNow;
        LastError = string.Empty;
    }

    public void Stop()
    {
        Running = false;
        executing = false;
        NextRun = null;
        executionCts?.Cancel();
        executionCts?.Dispose();
        executionCts = null;
    }

    public void Tick()
    {
        ProcessQueuedCommands();
        if (!Running || executing || NextRun == null)
            return;

        if (DateTime.UtcNow < NextRun.Value)
            return;

        RunMacroOnce();
    }

    private void RunMacroOnce()
    {
        executionCts = new CancellationTokenSource();
        executing = true;
        _ = Task.Run(async () =>
        {
            try
            {
                foreach (var action in config.Actions)
                {
                    executionCts.Token.ThrowIfCancellationRequested();
                    await ExecuteActionAsync(action, executionCts.Token);
                    var delaySeconds = Math.Max(0, config.ClampDelaySeconds());
                    if (delaySeconds > 0)
                        await Task.Delay(TimeSpan.FromSeconds(delaySeconds), executionCts.Token);
                }
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                chatGui.PrintError($"[ShoutRunner] {ex.Message}");
            }
            finally
            {
                executing = false;
                executionCts?.Dispose();
                executionCts = null;

                if (Running && config.RepeatEnabled)
                {
                    var interval = config.GetInterval();
                    NextRun = DateTime.UtcNow + interval;
                }
                else
                {
                    Stop();
                }
            }
        });
    }

    private async Task ExecuteActionAsync(MacroAction action, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(action.Payload))
            return;

        var payload = action.Payload.Trim();
        switch (action.Type)
        {
            case MacroActionType.Shout:
                await IssueECommonsCommandAsync(token, $"/shout {payload}", $"/sh {payload}");
                await WaitForCompletionAsync(action, token);
                break;
            case MacroActionType.Teleport:
                await IssueCommandAsync(token, $"/tp {payload}");
                await WaitForCompletionAsync(action, token);
                break;
            case MacroActionType.WorldVisit:
                // Prefer Lifestream plugin for world transfer; fall back to /visit.
                await ExecuteWorldVisitWithFallbackAsync(payload, token);
                break;
            case MacroActionType.DataCenterVisit:
                await IssueCommandAsync(token, $"/datacenter {payload}");
                await WaitForCompletionAsync(action, token);
                break;
        }
    }

    public void Dispose()
    {
        Stop();
    }

    public void RunSingleShoutTest(string text)
    {
        // Fire a one-off shout test via the same queue as macro actions.
        _ = IssueECommonsCommandAsync(CancellationToken.None, $"/shout {text}", $"/sh {text}");
    }

    private async Task WaitForCompletionAsync(MacroAction action, CancellationToken token)
    {
        // Rough heuristic waits for area transitions to complete for travel actions.
        // For shout, give a brief pause so messages are not spammed.
        var timeout = action.Type == MacroActionType.Shout ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(90);
        var endAt = DateTime.UtcNow + timeout;

        if (action.Type == MacroActionType.Shout)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), token);
            return;
        }

        var seenTransition = false;
        while (DateTime.UtcNow < endAt)
        {
            token.ThrowIfCancellationRequested();
            var transitioning = condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51];
            if (transitioning)
                seenTransition = true;

            // Require at least one transition event to be seen before we consider it done.
            if (seenTransition && !transitioning)
                return;

            await Task.Delay(200, token);
        }

        // Ensure we are fully loaded/logged in before continuing.
        await WaitUntilChatReadyAsync(token);
    }

    private string GetDataCenterForWorld(string world)
    {
        if (string.IsNullOrWhiteSpace(world))
            return string.Empty;

        return worldToDataCenter.TryGetValue(world.Trim(), out var dc)
            ? dc
            : string.Empty;
    }

    private async Task IssueCommandAsync(CancellationToken token, params string[] commands)
    {
        foreach (var cmd in commands)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(cmd))
                continue;

            await WaitUntilChatReadyAsync(token);
            chatGui.Print($"[ShoutRunner] Sending {cmd}");
            await EnqueueCommandAsync(cmd, useECommons: false, token);
        }
    }

    private async Task IssueECommonsCommandAsync(CancellationToken token, params string[] commands)
    {
        foreach (var cmd in commands)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(cmd))
                continue;

            await WaitUntilChatReadyAsync(token);
            chatGui.Print($"[ShoutRunner] Sending {cmd} via ECommons");
            await EnqueueCommandAsync(cmd, useECommons: true, token);
        }
    }

    private void TryInvokeChatSend(string text)
    {
        try
        {
            var methods = chatGui.GetType()
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(m => m.GetParameters().Length == 1
                            && (m.GetParameters()[0].ParameterType == typeof(string)
                                || m.GetParameters()[0].ParameterType.Name.Contains("SeString", StringComparison.OrdinalIgnoreCase))
                            && (m.Name.Contains("Send", StringComparison.OrdinalIgnoreCase)
                                || m.Name.Contains("Chat", StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (methods.Count == 0)
            {
                chatGui.Print("[ShoutRunner] No ChatGui send methods discovered for fallback.");
                return;
            }

            chatGui.Print($"[ShoutRunner] Trying {methods.Count} ChatGui fallback method(s)...");

            foreach (var method in methods)
            {
                try
                {
                    var param = method.GetParameters()[0];
                    object payload = text;
                    if (param.ParameterType != typeof(string))
                    {
                        var builder = new SeStringBuilder();
                        builder.AddText(text);
                        payload = builder.Build();
                    }

                    method.Invoke(chatGui, new[] { payload });
                    chatGui.Print($"[ShoutRunner] Fallback chat send via {method.Name}");
                    break;
                }
                catch
                {
                    // ignore and continue to next candidate
                }
            }

            chatGui.Print("[ShoutRunner] Finished fallback attempts.");
        }
        catch
        {
            // ignore
        }
    }

    private async Task WaitUntilChatReadyAsync(CancellationToken token)
    {
        // Avoid firing commands while logging out, transitioning, or not logged in.
        while (true)
        {
            token.ThrowIfCancellationRequested();

            if (!clientState.IsLoggedIn || clientState.LocalPlayer == null)
            {
                await Task.Delay(500, token);
                continue;
            }

            var blocked =
                condition[ConditionFlag.BetweenAreas] ||
                condition[ConditionFlag.BetweenAreas51] ||
                condition[ConditionFlag.LoggingOut] ||
                condition[ConditionFlag.OccupiedInCutSceneEvent] ||
                condition[ConditionFlag.OccupiedInQuestEvent] ||
                condition[ConditionFlag.OccupiedInEvent] ||
                condition[ConditionFlag.Occupied] ||
                condition[ConditionFlag.WatchingCutscene];

            if (!blocked)
                return;

            await Task.Delay(200, token);
        }
    }

    private async Task EnqueueCommandAsync(string command, bool useECommons, CancellationToken token)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reg = token.Register(() => tcs.TrySetCanceled(token));

        lock (commandLock)
        {
            commandQueue.Enqueue(new QueuedCommand(command, useECommons, tcs));
        }

        await tcs.Task;
    }

    private void ProcessQueuedCommands()
    {
        QueuedCommand? queued = null;
        lock (commandLock)
        {
            if (commandQueue.Count > 0)
                queued = commandQueue.Dequeue();
        }

        if (queued == null)
            return;

        try
        {
            if (queued.UseECommons)
            {
                Chat.ExecuteCommand(queued.Command);
            }
            else
            {
                commandManager.ProcessCommand(queued.Command);
                TryInvokeChatSend(queued.Command);
            }
            queued.Completion.TrySetResult();
        }
        catch (Exception ex)
        {
            chatGui.PrintError($"[ShoutRunner] Command error: {ex.Message}");
            queued.Completion.TrySetException(ex);
        }
    }

    private sealed class QueuedCommand
    {
        public string Command { get; }
        public bool UseECommons { get; }
        public TaskCompletionSource Completion { get; }

        public QueuedCommand(string command, bool useECommons, TaskCompletionSource completion)
        {
            Command = command;
            UseECommons = useECommons;
            Completion = completion;
        }
    }

    private async Task ExecuteWorldVisitWithFallbackAsync(string world, CancellationToken token)
    {
        var target = world.Trim();
        if (string.IsNullOrEmpty(target))
            return;

        var candidates = GetWorldFallbackList(target);
        foreach (var candidate in candidates)
        {
            chatGui.Print($"[ShoutRunner] World visit attempt: {candidate}");
            await IssueCommandAsync(token, $"/lifestream {candidate}", $"/visit {candidate}");

            if (await WaitForWorldArrivalAsync(candidate, token))
                return;

            chatGui.PrintError($"[ShoutRunner] World visit failed: {candidate}. Trying next...");
        }

        chatGui.PrintError("[ShoutRunner] All world visit attempts failed; staying on current world.");
    }

    private List<string> GetWorldFallbackList(string target)
    {
        var index = worldVisitOrder.FindIndex(w => string.Equals(w, target, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            return new List<string> { target };

        var ordered = new List<string>();
        for (var i = 0; i < worldVisitOrder.Count; i++)
        {
            var idx = (index + i) % worldVisitOrder.Count;
            ordered.Add(worldVisitOrder[idx]);
        }

        return ordered;
    }

    private async Task<bool> WaitForWorldArrivalAsync(string targetWorld, CancellationToken token)
    {
        var target = targetWorld.Trim();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(120);
        var seenTransition = false;

        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();

            if (!clientState.IsLoggedIn || clientState.LocalPlayer == null)
            {
                await Task.Delay(500, token);
                continue;
            }

            var transitioning = condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51];
            if (transitioning)
                seenTransition = true;

            if (!transitioning)
            {
                var currentWorld = clientState.LocalPlayer?.CurrentWorld.Value.Name.ToString() ?? string.Empty;
                if (!string.IsNullOrEmpty(currentWorld) && string.Equals(currentWorld, target, StringComparison.OrdinalIgnoreCase))
                    return true;

                if (seenTransition && !string.IsNullOrEmpty(currentWorld) && !string.Equals(currentWorld, target, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            await Task.Delay(500, token);
        }

        return false;
    }
}
