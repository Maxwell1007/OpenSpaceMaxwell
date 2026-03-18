using System;
using Content.Server._OpenSpace.Combat.Systems;

namespace Content.Server._OpenSpace.Combat.Components;

[RegisterComponent]
[Access(typeof(CorporateJudoMasterySystem))]
public sealed partial class CorporateJudoEyePokeBlurComponent : Component
{
    [ViewVariables]
    public TimeSpan ExpireAt;

    [ViewVariables]
    public bool Initialized;

    [ViewVariables]
    public int OriginalMinEyeDamage;

    [ViewVariables]
    public int AppliedMinEyeDamage;
}
