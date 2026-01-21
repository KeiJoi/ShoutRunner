using System;

namespace ShoutRunner;

public enum MacroActionType
{
    Shout,
    Teleport,
    WorldVisit,
    DataCenterVisit
}

public sealed class MacroAction
{
    public MacroActionType Type { get; set; }

    public string Payload { get; set; } = string.Empty;

    public string FriendlyName => Type switch
    {
        MacroActionType.Shout => "Shout",
        MacroActionType.Teleport => "Teleport",
        MacroActionType.WorldVisit => "World Transfer",
        MacroActionType.DataCenterVisit => "Data Center",
        _ => Type.ToString()
    };

    public string DisplayDetail => Type switch
    {
        MacroActionType.Shout => Payload,
        MacroActionType.Teleport => $"Teleport to \"{Payload}\"",
        MacroActionType.WorldVisit => $"Visit world \"{Payload}\"",
        MacroActionType.DataCenterVisit => $"Travel to data center \"{Payload}\"",
        _ => Payload
    };

    public MacroAction Clone()
    {
        return new MacroAction
        {
            Type = Type,
            Payload = Payload
        };
    }
}
