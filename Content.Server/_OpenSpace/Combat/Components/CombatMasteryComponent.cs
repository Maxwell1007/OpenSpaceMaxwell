using System.Collections.Generic;
using Content.Server._OpenSpace.Combat.Systems;

namespace Content.Server._OpenSpace.Combat.Components;

[RegisterComponent, ComponentProtoName("CombatMastery")]
[Access(typeof(CombatMasteryControllerSystem))]
public sealed partial class CombatMasteryComponent : Component
{
    [DataField]
    public int MaxComboLength = 12;

    [DataField]
    public List<ComboMasteryKeys> CombatMasteryCurrentCombo = new();

    [DataField]
    public EntityUid? CurrentTarget;
}
