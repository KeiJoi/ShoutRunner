using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace ShoutRunner;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public List<MacroAction> Actions { get; set; } = new();

    public List<MacroPreset> Presets { get; set; } = new();

    public bool RepeatEnabled { get; set; } = true;

    public int IntervalHours { get; set; } = 0;

    public int IntervalMinutes { get; set; } = 30;

    public int IntervalSeconds { get; set; } = 0;

    public int DelayBetweenActionsSeconds { get; set; } = 2;

    private IDalamudPluginInterface? pluginInterface;

    public void Initialize(IDalamudPluginInterface pluginInterface)
    {
        this.pluginInterface = pluginInterface;
    }

    public void Save()
    {
        pluginInterface?.SavePluginConfig(this);
    }

    public TimeSpan GetInterval()
    {
        var hours = Math.Max(0, IntervalHours);
        var minutes = Math.Max(0, IntervalMinutes);
        var seconds = Math.Max(0, IntervalSeconds);
        return new TimeSpan(hours, minutes, seconds);
    }

    public int ClampDelaySeconds()
    {
        if (DelayBetweenActionsSeconds < 0)
            DelayBetweenActionsSeconds = 0;
        if (DelayBetweenActionsSeconds > 120)
            DelayBetweenActionsSeconds = 120;
        return DelayBetweenActionsSeconds;
    }
}
