using System.Collections.Generic;

namespace Content.Server._OpenSpace.Combat.CombatMastery;

[ByRefEvent]
public sealed class CombatMasteryComboUpdatedEvent : EntityEventArgs
{
    public EntityUid Target { get; }
    public IReadOnlyList<ComboMasteryKeys> Combo { get; }
    public bool TemplateExecuted { get; set; }

    public CombatMasteryComboUpdatedEvent(EntityUid target, IReadOnlyList<ComboMasteryKeys> combo)
    {
        Target = target;
        Combo = combo;
    }
}
