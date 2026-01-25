using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using ECommons.Automation;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace ShoutRunner;

public sealed class MacroRunner : IDisposable
{
    private readonly Configuration config;
    private readonly ICommandManager commandManager;
    private readonly IChatGui chatGui;
    private readonly ICondition condition;
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly IDataManager dataManager;
    private readonly LifestreamIpc lifestreamIpc;
    private readonly IFramework framework;

    private readonly Dictionary<string, string> worldToDataCenter;
    private readonly List<string> worldVisitOrder;
    private readonly Queue<QueuedCommand> commandQueue = new();
    private readonly object commandLock = new();
    private readonly object progressLock = new();
    private float progressValue;
    private string progressLabel = string.Empty;
    private readonly Dictionary<uint, string> aetheryteNames = new();
    private readonly Dictionary<uint, string> territoryNames = new();
    private bool teleportDataLoaded;

    private CancellationTokenSource? executionCts;
    private bool executing;

    public bool Running { get; private set; }

    public DateTime? NextRun { get; private set; }

    public string LastError { get; private set; } = string.Empty;

    public bool TryGetProgress(out float value, out string label)
    {
        lock (progressLock)
        {
            value = progressValue;
            label = progressLabel;
            return !string.IsNullOrEmpty(progressLabel);
        }
    }

    public MacroRunner(Configuration config, ICommandManager commandManager, IChatGui chatGui, ICondition condition, IClientState clientState, IObjectTable objectTable, IDataManager dataManager, LifestreamIpc lifestreamIpc, IFramework framework)
    {
        this.config = config;
        this.commandManager = commandManager;
        this.chatGui = chatGui;
        this.condition = condition;
        this.clientState = clientState;
        this.objectTable = objectTable;
        this.dataManager = dataManager;
        this.lifestreamIpc = lifestreamIpc;
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
                var total = config.Actions.Count;
                for (var i = 0; i < total; i++)
                {
                    var action = config.Actions[i];
                    executionCts.Token.ThrowIfCancellationRequested();
                    SetProgress($"Action {i + 1}/{total}: {action.FriendlyName}", total == 0 ? 0f : (float)i / total);
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
                ClearProgress();

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
                await IssueECommonsCommandAsync(token, $"/shout {payload}");
                await WaitForCompletionAsync(action, token);
                break;
            case MacroActionType.Teleport:
                if (await TryTeleportAsync(payload, token))
                    await WaitForCompletionAsync(action, token);
                break;
            case MacroActionType.WorldVisit:
                await ExecuteWorldVisitWithFallbackAsync(payload, token);
                break;
            case MacroActionType.DataCenterVisit:
                await ExecuteDataCenterVisitAsync(payload, token);
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
        _ = IssueECommonsCommandAsync(CancellationToken.None, $"/shout {text}");
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
            var state = await GetGameStateAsync(token);
            var transitioning = state.BetweenAreas || state.BetweenAreas51;
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

    private void SetProgress(string label, float value)
    {
        lock (progressLock)
        {
            progressLabel = label;
            progressValue = Math.Clamp(value, 0f, 1f);
        }
    }

    private void ClearProgress()
    {
        lock (progressLock)
        {
            progressLabel = string.Empty;
            progressValue = 0f;
        }
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
            chatGui.Print($"[ShoutRunner] Sending {cmd} via ECommons");
            await EnqueueCommandAsync(cmd, useECommons: true, token);
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

            var state = await GetGameStateAsync(token);
            if (!state.IsLoggedIn || !state.HasLocalPlayer)
            {
                await Task.Delay(500, token);
                continue;
            }

            var blocked =
                state.BetweenAreas ||
                state.BetweenAreas51 ||
                state.LoggingOut ||
                state.OccupiedInCutSceneEvent ||
                state.OccupiedInQuestEvent ||
                state.OccupiedInEvent ||
                state.Occupied ||
                state.WatchingCutscene;

            if (!blocked)
                return;

            await Task.Delay(200, token);
        }
    }

    private async Task<bool> WaitForLifestreamReadyAsync(CancellationToken token)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();

            if (!lifestreamIpc.TryIsBusy(out var busy))
            {
                chatGui.PrintError("[ShoutRunner] Lifestream IPC not available. Install Lifestream for world/DC travel.");
                return false;
            }

            if (!busy)
                return true;

            await Task.Delay(500, token);
        }

        chatGui.PrintError("[ShoutRunner] Lifestream is busy; cannot start transfer.");
        return false;
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
            chatGui.Print($"[ShoutRunner] World visit attempt via Lifestream: {candidate}");
            if (await ExecuteWorldTransferAsync(candidate, token))
                return;

            chatGui.PrintError($"[ShoutRunner] World visit failed: {candidate}. Trying next...");
        }

        chatGui.PrintError("[ShoutRunner] All world visit attempts failed; staying on current world.");
    }

    private async Task ExecuteDataCenterVisitAsync(string dataCenter, CancellationToken token)
    {
        var target = dataCenter.Trim();
        if (string.IsNullOrEmpty(target))
            return;

        var world = GetWorldForDataCenter(target);
        if (string.IsNullOrEmpty(world))
        {
            chatGui.PrintError($"[ShoutRunner] Unknown data center: {target}");
            return;
        }

        chatGui.Print($"[ShoutRunner] Data center visit via Lifestream: {target} (using {world})");
        await ExecuteWorldTransferAsync(world, token);
    }

    private async Task<bool> ExecuteWorldTransferAsync(string targetWorld, CancellationToken token)
    {
        await WaitUntilChatReadyAsync(token);
        if (!await WaitForLifestreamReadyAsync(token))
            return false;

        // Determine if this is a cross-DC transfer
        var isCrossDC = false;
        if (lifestreamIpc.TryCanVisitCrossDC(targetWorld, out var crossDC))
        {
            isCrossDC = crossDC;
        }

        chatGui.Print($"[ShoutRunner] Lifestream transfer to {targetWorld} ({(isCrossDC ? "cross-DC" : "same-DC")})");
        if (!lifestreamIpc.TryChangeWorld(targetWorld))
        {
            chatGui.PrintError($"[ShoutRunner] Lifestream rejected transfer to {targetWorld}.");
            return false;
        }

        SetProgress($"Travel to {targetWorld}", 0.5f);

        // DC transfers require different waiting logic due to logout/login
        if (isCrossDC)
        {
            return await WaitForDataCenterTransferAsync(targetWorld, token);
        }
        else
        {
            return await WaitForWorldArrivalAsync(targetWorld, token);
        }
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

    private string GetWorldForDataCenter(string dataCenter)
    {
        if (string.IsNullOrWhiteSpace(dataCenter))
            return string.Empty;

        foreach (var world in worldVisitOrder)
        {
            if (string.Equals(GetDataCenterForWorld(world), dataCenter, StringComparison.OrdinalIgnoreCase))
                return world;
        }

        return string.Empty;
    }

    private async Task<bool> WaitForWorldArrivalAsync(string targetWorld, CancellationToken token)
    {
        var target = targetWorld.Trim();
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(3);
        var seenTransition = false;
        var lifestreamStarted = false;

        chatGui.Print($"[ShoutRunner] Waiting for world transfer to {target}...");

        // First, wait for Lifestream to become busy (it needs to TP to aetheryte, interact, etc.)
        var startDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < startDeadline)
        {
            token.ThrowIfCancellationRequested();

            if (lifestreamIpc.TryIsBusy(out var busy))
            {
                if (busy)
                {
                    lifestreamStarted = true;
                    chatGui.Print($"[ShoutRunner] Lifestream started processing world transfer");
                    break;
                }
            }

            await Task.Delay(500, token);
        }

        if (!lifestreamStarted)
        {
            chatGui.PrintError($"[ShoutRunner] Lifestream did not start processing the transfer");
            return false;
        }

        // Now wait for the actual transfer to complete
        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();

            var state = await GetGameStateAsync(token);
            if (!state.IsLoggedIn || !state.HasLocalPlayer)
            {
                await Task.Delay(500, token);
                continue;
            }

            // Check if we're already at the destination (might have been quick)
            var currentWorld = state.CurrentWorld;
            if (!string.IsNullOrEmpty(currentWorld) && string.Equals(currentWorld, target, StringComparison.OrdinalIgnoreCase))
            {
                // Wait for Lifestream to finish completely
                if (lifestreamIpc.TryIsBusy(out var busy) && !busy)
                {
                    chatGui.Print($"[ShoutRunner] Successfully arrived at {currentWorld}");
                    return true;
                }
            }

            var transitioning = state.BetweenAreas || state.BetweenAreas51;
            if (transitioning)
            {
                if (!seenTransition)
                {
                    seenTransition = true;
                    chatGui.Print($"[ShoutRunner] World transfer in progress...");
                }
            }

            if (!transitioning && seenTransition)
            {
                if (!string.IsNullOrEmpty(currentWorld) && string.Equals(currentWorld, target, StringComparison.OrdinalIgnoreCase))
                {
                    // Wait for Lifestream to finish
                    if (lifestreamIpc.TryIsBusy(out var busy) && !busy)
                    {
                        chatGui.Print($"[ShoutRunner] Successfully arrived at {currentWorld}");
                        return true;
                    }
                }
                else if (!string.IsNullOrEmpty(currentWorld))
                {
                    chatGui.PrintError($"[ShoutRunner] Transfer completed but arrived at {currentWorld} instead of {target}");
                    return false;
                }
            }

            await Task.Delay(500, token);
        }

        chatGui.PrintError($"[ShoutRunner] World transfer timed out after 3 minutes");
        return false;
    }

    private async Task<bool> WaitForDataCenterArrivalAsync(string targetDc, CancellationToken token)
    {
        var target = targetDc.Trim();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(150);
        var seenTransition = false;

        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            var state = await GetGameStateAsync(token);
            if (!state.IsLoggedIn || !state.HasLocalPlayer)
            {
                await Task.Delay(500, token);
                continue;
            }

            var transitioning = state.BetweenAreas || state.BetweenAreas51;
            if (transitioning)
                seenTransition = true;

            if (!transitioning)
            {
                var currentDc = GetDataCenterForWorld(state.CurrentWorld);
                if (!string.IsNullOrEmpty(currentDc) && string.Equals(currentDc, target, StringComparison.OrdinalIgnoreCase))
                    return true;

                if (seenTransition && !string.IsNullOrEmpty(currentDc) && !string.Equals(currentDc, target, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            await Task.Delay(500, token);
        }

        return false;
    }

    private async Task<bool> WaitForDataCenterTransferAsync(string targetWorld, CancellationToken token)
    {
        var target = targetWorld.Trim();
        // DC transfers take much longer due to logout/login cycle
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(5);
        var seenLogout = false;
        var lifestreamStartedBusy = false;

        chatGui.Print($"[ShoutRunner] Waiting for DC transfer to {target}...");

        // First, wait for Lifestream to start processing (may need to TP to aetheryte first)
        var startDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < startDeadline)
        {
            token.ThrowIfCancellationRequested();

            if (lifestreamIpc.TryIsBusy(out var busy) && busy)
            {
                lifestreamStartedBusy = true;
                chatGui.Print($"[ShoutRunner] Lifestream started processing DC transfer");
                break;
            }

            await Task.Delay(500, token);
        }

        if (!lifestreamStartedBusy)
        {
            chatGui.PrintError($"[ShoutRunner] Lifestream did not start processing the transfer. Make sure you're near a main aetheryte.");
            return false;
        }

        // Wait for the transfer to complete
        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();

            var state = await GetGameStateAsync(token);

            // Track if we've seen a logout (player will be logged out during DC transfer)
            if (!state.IsLoggedIn)
            {
                if (!seenLogout)
                {
                    seenLogout = true;
                    chatGui.Print($"[ShoutRunner] Player logged out for DC transfer");
                }
                await Task.Delay(1000, token);
                continue;
            }

            // If we're logged in (either still waiting for logout or returned after logout)
            if (state.IsLoggedIn && state.HasLocalPlayer)
            {
                var currentWorld = state.CurrentWorld;

                // Check if we're already at destination (maybe we were already there)
                if (!string.IsNullOrEmpty(currentWorld) && string.Equals(currentWorld, target, StringComparison.OrdinalIgnoreCase))
                {
                    if (lifestreamIpc.TryIsBusy(out var busy) && !busy)
                    {
                        chatGui.Print($"[ShoutRunner] Successfully arrived at {currentWorld}");
                        return true;
                    }
                }

                // If we've seen a logout and are now logged back in
                if (seenLogout)
                {
                    // Wait for Lifestream to finish all its tasks
                    if (lifestreamIpc.TryIsBusy(out var busy) && !busy)
                    {
                        // Give it a moment to fully settle
                        await Task.Delay(2000, token);

                        // Check final world
                        var finalState = await GetGameStateAsync(token);
                        if (finalState.IsLoggedIn && finalState.HasLocalPlayer)
                        {
                            currentWorld = finalState.CurrentWorld;
                            if (!string.IsNullOrEmpty(currentWorld) && string.Equals(currentWorld, target, StringComparison.OrdinalIgnoreCase))
                            {
                                chatGui.Print($"[ShoutRunner] Successfully arrived at {currentWorld}");
                                return true;
                            }
                            else if (!string.IsNullOrEmpty(currentWorld))
                            {
                                chatGui.PrintError($"[ShoutRunner] Transfer completed but arrived at {currentWorld} instead of {target}");
                                return false;
                            }
                        }
                    }
                }
            }

            await Task.Delay(1000, token);
        }

        chatGui.PrintError($"[ShoutRunner] DC transfer timed out after 5 minutes");
        return false;
    }

    private async Task<bool> TryTeleportAsync(string destination, CancellationToken token)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reg = token.Register(() => tcs.TrySetCanceled(token));

        _ = framework.RunOnFrameworkThread(() =>
        {
            try
            {
                EnsureTeleportDataLoaded();
                if (!TryFindTeleportInfo(destination, out var info, out var name))
                {
                    chatGui.PrintError($"[ShoutRunner] No attuned aetheryte found for \"{destination}\".");
                    tcs.TrySetResult(false);
                    return;
                }

                unsafe
                {
                    var localPlayer = Control.GetLocalPlayer();
                    if (localPlayer == null)
                    {
                        chatGui.PrintError("[ShoutRunner] Teleport failed: player not available.");
                        tcs.TrySetResult(false);
                        return;
                    }

                    var status = ActionManager.Instance()->GetActionStatus(ActionType.Action, 5);
                    if (status != 0)
                    {
                        chatGui.PrintError($"[ShoutRunner] Teleport not ready (status {status}).");
                        tcs.TrySetResult(false);
                        return;
                    }

                    var success = Telepo.Instance()->Teleport(info.AetheryteId, info.SubIndex);
                    if (success)
                        chatGui.Print($"[ShoutRunner] Teleporting to {name}.");
                    else
                        chatGui.PrintError($"[ShoutRunner] Teleport failed for {name}.");

                    tcs.TrySetResult(success);
                }
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        return await tcs.Task;
    }

    private void EnsureTeleportDataLoaded()
    {
        if (teleportDataLoaded)
            return;

        teleportDataLoaded = true;
        try
        {
            var sheet = dataManager.GetExcelSheet<Aetheryte>(clientState.ClientLanguage);
            if (sheet == null)
                return;

            foreach (var row in sheet)
            {
                var placeName = row.PlaceName.ValueNullable?.Name.ToString();
                if (!string.IsNullOrWhiteSpace(placeName))
                    aetheryteNames[row.RowId] = placeName;

                if (row.IsAetheryte)
                {
                    var territoryName = row.Territory.ValueNullable?.PlaceName.ValueNullable?.Name.ToString();
                    if (!string.IsNullOrWhiteSpace(territoryName))
                        territoryNames[row.RowId] = territoryName;
                }
            }
        }
        catch
        {
            aetheryteNames.Clear();
            territoryNames.Clear();
        }
    }

    private unsafe bool TryFindTeleportInfo(string destination, out TeleportInfo info, out string name)
    {
        info = default;
        name = destination;

        var dest = destination.Trim();
        if (string.IsNullOrEmpty(dest))
            return false;

        var tp = Telepo.Instance();
        if (tp == null || tp->UpdateAetheryteList() == null)
            return false;

        var count = tp->TeleportList.LongCount;
        if (count <= 0)
            return false;

        for (long i = 0; i < count; i++)
        {
            var entry = tp->TeleportList[i];
            if (aetheryteNames.TryGetValue(entry.AetheryteId, out var placeName)
                && string.Equals(placeName, dest, StringComparison.OrdinalIgnoreCase))
            {
                info = entry;
                name = placeName;
                return true;
            }
        }

        for (long i = 0; i < count; i++)
        {
            var entry = tp->TeleportList[i];
            if (territoryNames.TryGetValue(entry.AetheryteId, out var territoryName)
                && string.Equals(territoryName, dest, StringComparison.OrdinalIgnoreCase))
            {
                info = entry;
                name = aetheryteNames.TryGetValue(entry.AetheryteId, out var placeName) ? placeName : territoryName;
                return true;
            }
        }

        TeleportInfo? match = null;
        string? matchName = null;
        var matches = 0;

        for (long i = 0; i < count; i++)
        {
            var entry = tp->TeleportList[i];
            var placeName = aetheryteNames.TryGetValue(entry.AetheryteId, out var p) ? p : string.Empty;
            var territoryName = territoryNames.TryGetValue(entry.AetheryteId, out var t) ? t : string.Empty;

            if (!string.IsNullOrEmpty(placeName) &&
                (placeName.Contains(dest, StringComparison.OrdinalIgnoreCase) || dest.Contains(placeName, StringComparison.OrdinalIgnoreCase)))
            {
                matches++;
                if (matches == 1)
                {
                    match = entry;
                    matchName = placeName;
                }
                continue;
            }

            if (!string.IsNullOrEmpty(territoryName) &&
                (territoryName.Contains(dest, StringComparison.OrdinalIgnoreCase) || dest.Contains(territoryName, StringComparison.OrdinalIgnoreCase)))
            {
                matches++;
                if (matches == 1)
                {
                    match = entry;
                    matchName = !string.IsNullOrEmpty(placeName) ? placeName : territoryName;
                }
            }
        }

        if (matches == 1 && match.HasValue && matchName != null)
        {
            info = match.Value;
            name = matchName;
            return true;
        }

        if (matches > 1)
            chatGui.PrintError($"[ShoutRunner] Teleport destination \"{destination}\" is ambiguous.");

        return false;
    }

    private async Task<GameState> GetGameStateAsync(CancellationToken token)
    {
        var tcs = new TaskCompletionSource<GameState>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reg = token.Register(() => tcs.TrySetCanceled(token));

        _ = framework.RunOnFrameworkThread(() =>
        {
            try
            {
                var localPlayer = objectTable.LocalPlayer;
                var state = new GameState(
                    clientState.IsLoggedIn,
                    localPlayer != null,
                    condition[ConditionFlag.BetweenAreas],
                    condition[ConditionFlag.BetweenAreas51],
                    condition[ConditionFlag.LoggingOut],
                    condition[ConditionFlag.OccupiedInCutSceneEvent],
                    condition[ConditionFlag.OccupiedInQuestEvent],
                    condition[ConditionFlag.OccupiedInEvent],
                    condition[ConditionFlag.Occupied],
                    condition[ConditionFlag.WatchingCutscene],
                    localPlayer?.CurrentWorld.Value.Name.ToString() ?? string.Empty,
                    localPlayer?.HomeWorld.Value.Name.ToString() ?? string.Empty
                );
                tcs.TrySetResult(state);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        return await tcs.Task;
    }

    private sealed record GameState(
        bool IsLoggedIn,
        bool HasLocalPlayer,
        bool BetweenAreas,
        bool BetweenAreas51,
        bool LoggingOut,
        bool OccupiedInCutSceneEvent,
        bool OccupiedInQuestEvent,
        bool OccupiedInEvent,
        bool Occupied,
        bool WatchingCutscene,
        string CurrentWorld,
        string HomeWorld);

}
