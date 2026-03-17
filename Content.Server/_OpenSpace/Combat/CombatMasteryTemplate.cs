using System.Collections.Generic;

namespace Content.Server._OpenSpace.Combat;

[DataDefinition]
public sealed partial class CombatMasteryTemplate
{
    [DataField(required: true)]
    public string Name = string.Empty;

    [DataField(required: true)]
    public List<ComboMasteryKeys> Sequence = [];

    public bool IsValid()
    {
        return !string.IsNullOrWhiteSpace(Name) && Sequence.Count > 0;
    }
}
