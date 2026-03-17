using System.Numerics;
using Content.Server._OpenSpace.Combat.Components;
using Content.Shared.Bed.Sleep;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Systems;
using Content.Shared.FixedPoint;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.StatusEffectNew;
using Content.Shared.Stunnable;
using Content.Shared.Throwing;
using Robust.Server.GameObjects;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.Server._OpenSpace.Combat.Systems;

public sealed class CloseQuarterCombatMasterySystem : CombatMasteryTemplateCollectionSystem<CloseQuarterCombatMasteryComponent>
{
    [Dependency] private readonly DamageableSystem _damageable = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly StatusEffectsSystem _statusEffects = default!;
    [Dependency] private readonly SharedStaminaSystem _stamina = default!;
    [Dependency] private readonly SharedStunSystem _stun = default!;
    [Dependency] private readonly ThrowingSystem _throwing = default!;
    [Dependency] private readonly TransformSystem _transform = default!;

    protected override void OnTemplateMatched(Entity<CloseQuarterCombatMasteryComponent> ent, EntityUid target, CombatMasteryTemplate template)
    {
        switch (template.Name)
        {
            case CloseQuarterCombatMasteryComponent.SlamTemplateName:
                DoSlam(ent.Owner, target, ent.Comp);
                break;
            case CloseQuarterCombatMasteryComponent.CQCKickTemplateName:
                DoCQCKick(ent.Owner, target, ent.Comp);
                break;
            case CloseQuarterCombatMasteryComponent.RestrainTemplateName:
                DoRestrain(ent.Owner, target, ent.Comp);
                break;
            case CloseQuarterCombatMasteryComponent.PressureTemplateName:
                DoPressure(ent.Owner, target, ent.Comp);
                break;
            case CloseQuarterCombatMasteryComponent.ConsecutiveCQCTemplateName:
                DoConsecutiveCqc(ent.Owner, target, ent.Comp);
                break;
        }
    }

    private void DoSlam(EntityUid user, EntityUid target, CloseQuarterCombatMasteryComponent component)
    {
        if (TerminatingOrDeleted(target) || IsTargetStunned(target))
            return;

        ApplyBluntDamage(user, target, component, component.SlamBluntDamage);
        _stun.TryKnockdown(target, component.SlamKnockdownDuration, refresh: true, autoStand: true, drop: true, force: true);
    }

    private void DoCQCKick(EntityUid user, EntityUid target, CloseQuarterCombatMasteryComponent component)
    {
        if (TerminatingOrDeleted(target))
            return;

        if (IsTargetStunned(target))
        {
            ApplyBluntDamage(user, target, component, component.CQCKickStunnedBluntDamage);
            _statusEffects.TryAddStatusEffectDuration(target, SleepingSystem.StatusEffectForcedSleeping, component.CQCKickSleepDuration);
            return;
        }

        ApplyBluntDamage(user, target, component, component.CQCKickBluntDamage);
        ThrowAwayFromUser(user, target, component.CQCKickThrowDistance, component.CQCKickThrowSpeed);
    }

    private void DoRestrain(EntityUid user, EntityUid target, CloseQuarterCombatMasteryComponent component)
    {
        if (TerminatingOrDeleted(target))
            return;

        _stamina.TakeStaminaDamage(target, component.RestrainStaminaDamage, source: user);
        _stun.TryKnockdown(target, component.RestrainKnockdownDuration, refresh: true, autoStand: true, drop: true, force: true);
    }

    private void DoPressure(EntityUid user, EntityUid target, CloseQuarterCombatMasteryComponent component)
    {
        if (TerminatingOrDeleted(target))
            return;

        _stamina.TakeStaminaDamage(target, component.PressureStaminaDamage, source: user);
    }

    private void DoConsecutiveCqc(EntityUid user, EntityUid target, CloseQuarterCombatMasteryComponent component)
    {
        if (TerminatingOrDeleted(target) || IsTargetStunned(target))
            return;

        ApplyBluntDamage(user, target, component, component.ConsecutiveCqcBluntDamage);
        _stamina.TakeStaminaDamage(target, component.ConsecutiveCqcStaminaDamage, source: user);
        TryStealActiveHandItem(user, target);
    }

    private void ApplyBluntDamage(EntityUid user, EntityUid target, CloseQuarterCombatMasteryComponent component, float amount)
    {
        if (!TryComp<DamageableComponent>(target, out _))
            return;

        var bluntDamage = new DamageSpecifier(_prototypeManager.Index(component.BluntDamageType), FixedPoint2.New(amount));
        _damageable.TryChangeDamage(target, bluntDamage, ignoreResistances: true, origin: user);
    }

    private void ThrowAwayFromUser(EntityUid user, EntityUid target, float distance, float speed)
    {
        var userPos = _transform.GetWorldPosition(Transform(user));
        var targetPos = _transform.GetWorldPosition(Transform(target));
        var direction = targetPos - userPos;

        if (direction == Vector2.Zero)
            direction = Transform(user).LocalRotation.ToWorldVec();

        if (direction == Vector2.Zero)
            return;

        var throwVector = direction.Normalized() * distance;
        _throwing.TryThrow(target, throwVector, speed, user, compensateFriction: true, doSpin: false);
    }

    private void TryStealActiveHandItem(EntityUid user, EntityUid target)
    {
        if (!TryComp<HandsComponent>(target, out var targetHands))
            return;

        if (!_hands.TryGetActiveItem((target, targetHands), out var stolenItem))
            return;

        if (!_hands.TryDrop((target, targetHands), stolenItem.Value, checkActionBlocker: false, doDropInteraction: false))
            return;

        if (!TryComp<HandsComponent>(user, out var userHands))
            return;

        var activeHand = _hands.GetActiveHand((user, userHands));
        if (activeHand == null)
            return;

        _hands.TryForcePickup((user, userHands), stolenItem.Value, activeHand, checkActionBlocker: false, animate: false);
    }

    private bool IsTargetStunned(EntityUid target)
    {
        return HasComp<StunnedComponent>(target)
               || HasComp<KnockedDownComponent>(target)
               || HasComp<SleepingComponent>(target);
    }
}
