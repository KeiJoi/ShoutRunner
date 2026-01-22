using System.Collections.Generic;

namespace ShoutRunner;

public sealed class MacroPreset
{
    public string Name { get; set; } = string.Empty;

    public List<MacroAction> Actions { get; set; } = new();
}
