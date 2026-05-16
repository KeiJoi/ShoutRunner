using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using ECommons.Automation;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
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
    private readonly object transferMonitorLock = new();
    private string monitoredTransferWorld = string.Empty;
    private int monitoredTransferCongestionCount;
    private bool monitoredTransferShouldSkip;
    private const int EscapeVirtualKey = 0x1B;

    private CancellationTokenSource? executionCts;
    private bool executing;

    public bool Running { get; private set; }

    public DateTime? NextRun { get; private set; }

    public string LastError { get; private set; } = string.Empty;

    private enum ActionExecutionResult
    {
        Continue,
        SkipToNextWorldTransfer
    }

    private enum TransferExecutionResult
    {
        Success,
        Failed,
        SkipToNextWorldTransfer
    }

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
        chatGui.ChatMessageUnhandled += OnChatMessageUnhandled;

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
            ["Cuchulainn"] = "Dynamis",
            ["Golem"] = "Dynamis",
            ["Kraken"] = "Dynamis",
            ["Maduin"] = "Dynamis",
            ["Marilith"] = "Dynamis",
            ["Rafflesia"] = "Dynamis",
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
            "Cuchulainn",
            "Golem",
            "Kraken",
            "Maduin",
            "Marilith",
            "Rafflesia",
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
        EndTransferMonitoring();
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
                    var result = await ExecuteActionAsync(action, executionCts.Token);
                    if (result == ActionExecutionResult.SkipToNextWorldTransfer)
                    {
                        var nextTransferIndex = FindNextWorldTransferIndex(i + 1);
                        if (nextTransferIndex < 0)
                            break;

                        i = nextTransferIndex - 1;
                        continue;
                    }

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

    private int FindNextWorldTransferIndex(int startIndex)
    {
        for (var i = Math.Max(0, startIndex); i < config.Actions.Count; i++)
        {
            var type = config.Actions[i].Type;
            if (type == MacroActionType.WorldVisit || type == MacroActionType.DataCenterVisit)
                return i;
        }

        return -1;
    }

    private async Task<ActionExecutionResult> ExecuteActionAsync(MacroAction action, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(action.Payload))
            return ActionExecutionResult.Continue;

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
                return await ExecuteWorldVisitWithFallbackAsync(payload, token);
            case MacroActionType.DataCenterVisit:
                await ExecuteDataCenterVisitAsync(payload, token);
                break;
        }

        return ActionExecutionResult.Continue;
    }

    public void Dispose()
    {
        chatGui.ChatMessageUnhandled -= OnChatMessageUnhandled;
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

    private async Task<ActionExecutionResult> ExecuteWorldVisitWithFallbackAsync(string world, CancellationToken token)
    {
        var target = world.Trim();
        if (string.IsNullOrEmpty(target))
            return ActionExecutionResult.Continue;

        var candidates = GetWorldFallbackList(target);
        foreach (var candidate in candidates)
        {
            chatGui.Print($"[ShoutRunner] World visit attempt via Lifestream: {candidate}");
            var result = await ExecuteWorldTransferAsync(candidate, token);
            if (result == TransferExecutionResult.Success)
                return ActionExecutionResult.Continue;
            if (result == TransferExecutionResult.SkipToNextWorldTransfer)
                return ActionExecutionResult.SkipToNextWorldTransfer;

            lifestreamIpc.TryAbort();
            chatGui.PrintError($"[ShoutRunner] World visit failed: {candidate}. Trying next...");
        }

        chatGui.PrintError("[ShoutRunner] All world visit attempts failed; staying on current world.");
        return ActionExecutionResult.Continue;
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

    private void BeginTransferMonitoring(string targetWorld)
    {
        lock (transferMonitorLock)
        {
            monitoredTransferWorld = targetWorld.Trim();
            monitoredTransferCongestionCount = 0;
            monitoredTransferShouldSkip = false;
        }
    }

    private void EndTransferMonitoring()
    {
        lock (transferMonitorLock)
        {
            monitoredTransferWorld = string.Empty;
            monitoredTransferCongestionCount = 0;
            monitoredTransferShouldSkip = false;
        }
    }

    private bool ShouldSkipCurrentTransfer()
    {
        lock (transferMonitorLock)
        {
            return monitoredTransferShouldSkip;
        }
    }

    private async Task DismissTransferUiAsync(CancellationToken token)
    {
        for (var i = 0; i < 5; i++)
        {
            token.ThrowIfCancellationRequested();
            await TryDismissTransferUiOnceAsync(token);

            await Task.Delay(250, token);
        }
    }

    private async Task TryDismissTransferUiOnceAsync(CancellationToken token)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reg = token.Register(() => tcs.TrySetCanceled(token));

        _ = framework.RunOnFrameworkThread(() =>
        {
            try
            {
                unsafe
                {
                    var agent = AgentWorldTravel.Instance();
                    if (agent != null)
                    {
                        if (agent->IsAddonShown() || agent->IsAgentActive() || agent->IsAddonReady())
                        {
                            agent->HideAddon();
                            agent->Hide();
                        }
                    }
                }

                WindowsKeypress.SendKeypress(EscapeVirtualKey);
                tcs.TrySetResult();
            }
            catch
            {
                tcs.TrySetResult();
            }
        });

        await tcs.Task;
    }

    private void OnChatMessageUnhandled(IChatMessage message)
    {
        var text = message.Message.TextValue;
        if (string.IsNullOrWhiteSpace(text)
            || text.StartsWith("[ShoutRunner]", StringComparison.OrdinalIgnoreCase)
            || !Running
            || !IsCongestedTransferMessage(text))
            return;

        lock (transferMonitorLock)
        {
            if (string.IsNullOrWhiteSpace(monitoredTransferWorld))
                return;

            if (monitoredTransferShouldSkip)
                return;

            monitoredTransferCongestionCount++;
            if (monitoredTransferCongestionCount >= 2)
            {
                monitoredTransferShouldSkip = true;
                chatGui.PrintError($"[ShoutRunner] Congested-world response detected twice for {monitoredTransferWorld}. Aborting current Lifestream task.");
                lifestreamIpc.TryAbort();
            }
            else
            {
                chatGui.Print($"[ShoutRunner] Congested-world response detected for {monitoredTransferWorld} ({monitoredTransferCongestionCount}/2).");
            }
        }
    }

    private static bool IsCongestedTransferMessage(string text)
    {
        return text.Contains("This World is experiencing congestion. Character movement is limited at this time", StringComparison.OrdinalIgnoreCase)
            || text.Contains("currently congested", StringComparison.OrdinalIgnoreCase)
            || text.Contains("destination world is currently congested", StringComparison.OrdinalIgnoreCase)
            || text.Contains("please wait until the world has become less congested", StringComparison.OrdinalIgnoreCase)
            || text.Contains("world is currently full", StringComparison.OrdinalIgnoreCase)
            || text.Contains("please wait until an opening is available and try again", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<TransferExecutionResult> ExecuteWorldTransferAsync(string targetWorld, CancellationToken token)
    {
        await WaitUntilChatReadyAsync(token);
        if (!await WaitForLifestreamReadyAsync(token))
            return TransferExecutionResult.Failed;

        // Determine if this is a cross-DC transfer
        var isCrossDC = false;
        if (lifestreamIpc.TryCanVisitCrossDC(targetWorld, out var crossDC))
        {
            isCrossDC = crossDC;
        }

        BeginTransferMonitoring(targetWorld);
        try
        {
            chatGui.Print($"[ShoutRunner] Lifestream transfer to {targetWorld} ({(isCrossDC ? "cross-DC" : "same-DC")})");

            // Use Lifestream's command directly so we can see any error messages
            chatGui.Print($"[ShoutRunner] Executing: /li {targetWorld}");
            await IssueECommonsCommandAsync(token, $"/li {targetWorld}");

            // Give Lifestream a moment to process the command
            await Task.Delay(1000, token);

            SetProgress($"Travel to {targetWorld}", 0.5f);

            TransferExecutionResult result;
            if (isCrossDC)
            {
                result = await WaitForDataCenterTransferAsync(targetWorld, token);
            }
            else
            {
                result = await WaitForWorldArrivalAsync(targetWorld, token);
            }

            if (result == TransferExecutionResult.SkipToNextWorldTransfer)
            {
                lifestreamIpc.TryAbort();
                await DismissTransferUiAsync(token);
                chatGui.PrintError($"[ShoutRunner] {targetWorld} reported congestion twice. Waiting 5 seconds and skipping to the next world transfer.");
                await Task.Delay(TimeSpan.FromSeconds(5), token);
                return TransferExecutionResult.SkipToNextWorldTransfer;
            }

            if (result == TransferExecutionResult.Failed)
            {
                lifestreamIpc.TryAbort();
                await DismissTransferUiAsync(token);
            }

            return result;
        }
        finally
        {
            EndTransferMonitoring();
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

    private async Task<TransferExecutionResult> WaitForWorldArrivalAsync(string targetWorld, CancellationToken token)
    {
        var target = targetWorld.Trim();
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(3);
        var seenTransition = false;
        var lifestreamStarted = false;

        chatGui.Print($"[ShoutRunner] Waiting for world transfer to {target}...");

        // Wait briefly for Lifestream to become busy or for something to happen
        var startDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < startDeadline)
        {
            token.ThrowIfCancellationRequested();

            var state = await GetGameStateAsync(token);

            // Check if Lifestream is processing
            if (lifestreamIpc.TryIsBusy(out var busy) && busy)
            {
                lifestreamStarted = true;
                chatGui.Print($"[ShoutRunner] Lifestream started processing world transfer");
                break;
            }

            // Check if we're already transitioning (Lifestream might be moving us)
            if (state.BetweenAreas || state.BetweenAreas51)
            {
                lifestreamStarted = true;
                seenTransition = true;
                chatGui.Print($"[ShoutRunner] World transfer in progress...");
                break;
            }

            // Check if we're already at the destination
            if (state.IsLoggedIn && state.HasLocalPlayer)
            {
                var currentWorld = state.CurrentWorld;
                if (!string.IsNullOrEmpty(currentWorld) && string.Equals(currentWorld, target, StringComparison.OrdinalIgnoreCase))
                {
                    chatGui.Print($"[ShoutRunner] Already at {currentWorld}");
                    return TransferExecutionResult.Success;
                }
            }

            if (ShouldSkipCurrentTransfer())
                return TransferExecutionResult.SkipToNextWorldTransfer;

            await Task.Delay(500, token);
        }

        if (!lifestreamStarted)
        {
            chatGui.Print($"[ShoutRunner] Lifestream may be working (not reporting busy). Continuing to wait for transfer...");
        }

        // Now wait for the actual transfer to complete
        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();

            var state = await GetGameStateAsync(token);
            if (!state.IsLoggedIn || !state.HasLocalPlayer)
            {
                if (ShouldSkipCurrentTransfer())
                    return TransferExecutionResult.SkipToNextWorldTransfer;

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
                    return TransferExecutionResult.Success;
                }
            }

            if (ShouldSkipCurrentTransfer())
                return TransferExecutionResult.SkipToNextWorldTransfer;

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
                        return TransferExecutionResult.Success;
                    }
                }
                else if (!string.IsNullOrEmpty(currentWorld))
                {
                    chatGui.PrintError($"[ShoutRunner] Transfer completed but arrived at {currentWorld} instead of {target}");
                    return TransferExecutionResult.Failed;
                }
            }

            await Task.Delay(500, token);
        }

        chatGui.PrintError($"[ShoutRunner] World transfer timed out after 3 minutes");
        return TransferExecutionResult.Failed;
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

    private async Task<TransferExecutionResult> WaitForDataCenterTransferAsync(string targetWorld, CancellationToken token)
    {
        var target = targetWorld.Trim();
        // DC transfers take much longer due to logout/login cycle
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(5);
        var seenLogout = false;
        var lifestreamStartedBusy = false;

        chatGui.Print($"[ShoutRunner] Waiting for DC transfer to {target}...");

        // Wait briefly for Lifestream to start processing (may need to TP to aetheryte first)
        var startDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < startDeadline)
        {
            token.ThrowIfCancellationRequested();

            var state = await GetGameStateAsync(token);

            // Check if Lifestream is processing
            if (lifestreamIpc.TryIsBusy(out var busy) && busy)
            {
                lifestreamStartedBusy = true;
                chatGui.Print($"[ShoutRunner] Lifestream started processing DC transfer");
                break;
            }

            // Check if we're already logged out (DC transfer started)
            if (!state.IsLoggedIn)
            {
                lifestreamStartedBusy = true;
                seenLogout = true;
                chatGui.Print($"[ShoutRunner] Player logged out for DC transfer");
                break;
            }

            // Check if we're already at destination
            if (state.IsLoggedIn && state.HasLocalPlayer)
            {
                var currentWorld = state.CurrentWorld;
                if (!string.IsNullOrEmpty(currentWorld) && string.Equals(currentWorld, target, StringComparison.OrdinalIgnoreCase))
                {
                    chatGui.Print($"[ShoutRunner] Already at {currentWorld}");
                    return TransferExecutionResult.Success;
                }
            }

            if (ShouldSkipCurrentTransfer())
                return TransferExecutionResult.SkipToNextWorldTransfer;

            await Task.Delay(500, token);
        }

        if (!lifestreamStartedBusy)
        {
            chatGui.Print($"[ShoutRunner] Lifestream may be working (not reporting busy). Continuing to wait for DC transfer...");
        }

        // Wait for the transfer to complete
        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();

            var state = await GetGameStateAsync(token);

            // Track if we've seen a logout (player will be logged out during DC transfer)
            if (!state.IsLoggedIn)
            {
                if (ShouldSkipCurrentTransfer())
                    return TransferExecutionResult.SkipToNextWorldTransfer;

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
                        return TransferExecutionResult.Success;
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
                                return TransferExecutionResult.Success;
                            }
                            else if (!string.IsNullOrEmpty(currentWorld))
                            {
                                chatGui.PrintError($"[ShoutRunner] Transfer completed but arrived at {currentWorld} instead of {target}");
                                return TransferExecutionResult.Failed;
                            }
                        }
                    }
                }
            }

            if (ShouldSkipCurrentTransfer())
                return TransferExecutionResult.SkipToNextWorldTransfer;

            await Task.Delay(1000, token);
        }

        chatGui.PrintError($"[ShoutRunner] DC transfer timed out after 5 minutes");
        return TransferExecutionResult.Failed;
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
