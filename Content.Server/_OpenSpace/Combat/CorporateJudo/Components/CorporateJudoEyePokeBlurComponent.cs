using System;
using Content.Server._OpenSpace.Combat.CorporateJudo.Systems;

namespace Content.Server._OpenSpace.Combat.CorporateJudo.Components;

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
