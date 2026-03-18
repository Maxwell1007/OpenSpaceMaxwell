using System.Numerics;
using Content.Server._OpenSpace.Combat;
using Content.Server._OpenSpace.Combat.Components;
using Content.Shared.Bed.Sleep;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Systems;
using Content.Shared.FixedPoint;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.IdentityManagement;
using Content.Shared.Interaction.Events;
using Content.Shared.Inventory.VirtualItem;
using Content.Shared.Jittering;
using Content.Shared.Movement.Pulling.Components;
using Content.Shared.Movement.Pulling.Systems;
using Content.Shared.Popups;
using Content.Shared.Standing;
using Content.Shared.StatusEffectNew;
using Content.Shared.Stunnable;
using Content.Shared.Throwing;
using Content.Shared.Weapons.Melee.Events;
using Content.Shared._Starlight.Medical.Damage;
using Robust.Server.GameObjects;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._OpenSpace.Combat.Systems;

public sealed class CloseQuarterCombatMasterySystem : CombatMasteryTemplateCollectionSystem<CloseQuarterCombatMasteryComponent>
{
    [Dependency] private readonly DamageableSystem _damageable = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly SharedJitteringSystem _jittering = default!;
    [Dependency] private readonly PullingSystem _pulling = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly StandingStateSystem _standing = default!;
    [Dependency] private readonly StatusEffectsSystem _statusEffects = default!;
    [Dependency] private readonly SharedStaminaSystem _stamina = default!;
    [Dependency] private readonly SharedStunSystem _stun = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly ThrowingSystem _throwing = default!;
    [Dependency] private readonly TransformSystem _transform = default!;

    private static readonly TimeSpan PendingDamageTimeout = TimeSpan.FromSeconds(1);
    private readonly Dictionary<EntityUid, PendingMeleeDamageChange> _pendingMeleeDamage = [];

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<CloseQuarterCombatMasteryComponent, AttackAttemptEvent>(OnAttackAttempt,
            before: [typeof(CombatMasteryControllerSystem)]);
        SubscribeLocalEvent<CloseQuarterCombatMasteryComponent, CombatMasteryComboUpdatedEvent>(OnComboUpdated);
        SubscribeLocalEvent<DamageableComponent, AttackedEvent>(OnMeleeAttacked);
        SubscribeLocalEvent<DamageableComponent, DamageBeforeApplyEvent>(OnDamageBeforeApply);
    }

    protected override void OnTemplateMatched(Entity<CloseQuarterCombatMasteryComponent> ent, EntityUid target, CombatMasteryTemplate template)
    {
        switch (template.Name)
        {
            case CloseQuarterCombatMasteryComponent.SlamTemplateName:
                if (DoSlam(ent.Owner, target, ent.Comp))
                    PopupTechnique(ent.Owner, target, "cqc-slam-attacker-popup", "cqc-slam-target-popup", true);
                break;
            case CloseQuarterCombatMasteryComponent.CQCKickTemplateName:
                if (DoCQCKick(ent.Owner, target, ent.Comp))
                    PopupTechnique(ent.Owner, target, "cqc-kick-attacker-popup", "cqc-kick-target-popup");
                break;
            case CloseQuarterCombatMasteryComponent.RestrainTemplateName:
                if (DoRestrain(ent.Owner, target, ent.Comp))
                    PopupTechnique(ent.Owner, target, "cqc-restrain-attacker-popup", "cqc-restrain-target-popup", true);
                break;
            case CloseQuarterCombatMasteryComponent.PressureTemplateName:
                if (DoPressure(ent.Owner, target, ent.Comp))
                    PopupTechnique(ent.Owner, target, "cqc-pressure-attacker-popup", "cqc-pressure-target-popup");
                break;
            case CloseQuarterCombatMasteryComponent.ConsecutiveCQCTemplateName:
                if (DoConsecutiveCqc(ent.Owner, target, ent.Comp))
                    PopupTechnique(ent.Owner, target, "cqc-consecutive-attacker-popup", "cqc-consecutive-target-popup", true);
                break;
        }
    }

    private void OnAttackAttempt(Entity<CloseQuarterCombatMasteryComponent> ent, ref AttackAttemptEvent args)
    {
        if (args.Cancelled || args.Uid != ent.Owner || !args.Disarm || args.Target is not { } target)
            return;

        if (!CanUseRestrainFollowup(ent, target))
            return;

        if (!TryComp<PullerComponent>(ent.Owner, out var puller) ||
            puller.Pulling != target ||
            puller.GrabStage == GrabStage.None)
        {
            return;
        }

        ExecuteRestrainFollowup(ent, target);
        args.Cancel();
    }

    private void OnComboUpdated(Entity<CloseQuarterCombatMasteryComponent> ent, ref CombatMasteryComboUpdatedEvent args)
    {
        if (!ent.Comp.RestrainFollowupReady)
            return;

        if (ent.Comp.SkipNextComboResetForRestrain)
        {
            ent.Comp.SkipNextComboResetForRestrain = false;
            return;
        }

        ResetRestrainFollowup(ent.Comp);
    }

    private void OnMeleeAttacked(Entity<DamageableComponent> ent, ref AttackedEvent args)
    {
        var hasChanges = false;
        var pending = new PendingMeleeDamageChange
        {
            Origin = args.User,
            ExpiresAt = _timing.CurTime + PendingDamageTimeout,
        };

        if (TryComp<CloseQuarterCombatMasteryComponent>(args.User, out var attackerCqc) &&
            IsUnarmedMeleeAttack(args))
        {
            var targetDown = IsEntityDown(ent.Owner);
            var desiredDamage = targetDown
                ? attackerCqc.UnarmedDownedTargetDamage
                : attackerCqc.UnarmedDamage;

            if (IsEntityDown(args.User) && !targetDown)
            {
                desiredDamage += attackerCqc.ProneAttackerBonusDamage;
                _stun.TryKnockdown(ent.Owner,
                    attackerCqc.ProneAttackerKnockdownDuration,
                    refresh: true,
                    autoStand: true,
                    drop: true,
                    voluntary: true);
            }

            pending.DesiredDamage = desiredDamage;
            hasChanges = true;
        }

        if (TryComp<CloseQuarterCombatMasteryComponent>(ent.Owner, out var defenderCqc) &&
            _random.Prob(defenderCqc.DefensiveMeleeNullifyChance))
        {
            pending.Nullify = true;
            hasChanges = true;

            if (HasRealActiveItem(args.User))
            {
                _stun.TryUpdateStunDuration(args.User, defenderCqc.DefensiveMeleeCounterKnockdownDuration);
                _stun.TryKnockdown(args.User,
                    defenderCqc.DefensiveMeleeCounterKnockdownDuration,
                    refresh: true,
                    autoStand: true,
                    drop: true,
                    force: true);
            }
        }

        if (!hasChanges)
            return;

        _pendingMeleeDamage[ent.Owner] = pending;
    }

    private void OnDamageBeforeApply(Entity<DamageableComponent> ent, ref DamageBeforeApplyEvent args)
    {
        if (!_pendingMeleeDamage.TryGetValue(ent.Owner, out var pending))
            return;

        if (_timing.CurTime > pending.ExpiresAt)
        {
            _pendingMeleeDamage.Remove(ent.Owner);
            return;
        }

        if (args.Origin != pending.Origin)
            return;

        _pendingMeleeDamage.Remove(ent.Owner);

        if (pending.Nullify)
        {
            args.Damage = new DamageSpecifier();
            return;
        }

        if (pending.DesiredDamage is not { } desiredDamage)
            return;

        args.Damage = ScaleDamageToTotal(args.Damage, desiredDamage);
    }

    private bool DoSlam(EntityUid user, EntityUid target, CloseQuarterCombatMasteryComponent component)
    {
        if (TerminatingOrDeleted(target) || IsTargetStunned(target))
            return false;

        ApplyBluntDamage(user, target, component, component.SlamBluntDamage);
        _stun.TryKnockdown(target, component.SlamKnockdownDuration, refresh: true, autoStand: true, drop: true, force: true);
        return true;
    }

    private bool DoCQCKick(EntityUid user, EntityUid target, CloseQuarterCombatMasteryComponent component)
    {
        if (TerminatingOrDeleted(target))
            return false;

        if (IsTargetStunned(target))
        {
            ApplyBluntDamage(user, target, component, component.CQCKickStunnedBluntDamage);
            _statusEffects.TryAddStatusEffectDuration(target, SleepingSystem.StatusEffectForcedSleeping, component.CQCKickSleepDuration);
            return true;
        }

        ApplyBluntDamage(user, target, component, component.CQCKickBluntDamage);
        ThrowAwayFromUser(user, target, component.CQCKickThrowDistance, component.CQCKickThrowSpeed);
        return true;
    }

    private bool DoRestrain(EntityUid user, EntityUid target, CloseQuarterCombatMasteryComponent component)
    {
        if (TerminatingOrDeleted(target))
            return false;

        _stamina.TakeStaminaDamage(target, component.RestrainStaminaDamage, source: user);
        _stun.TryKnockdown(target, component.RestrainKnockdownDuration, refresh: true, autoStand: true, drop: true, force: true);

        component.RestrainFollowupReady = true;
        component.SkipNextComboResetForRestrain = true;
        component.RestrainFollowupTarget = target;
        component.RestrainFollowupExpireAt = _timing.CurTime + component.RestrainFollowupWindow;
        return true;
    }

    private bool DoPressure(EntityUid user, EntityUid target, CloseQuarterCombatMasteryComponent component)
    {
        if (TerminatingOrDeleted(target))
            return false;

        _stamina.TakeStaminaDamage(target, component.PressureStaminaDamage, source: user);
        return true;
    }

    private bool DoConsecutiveCqc(EntityUid user, EntityUid target, CloseQuarterCombatMasteryComponent component)
    {
        if (TerminatingOrDeleted(target) || IsTargetStunned(target))
            return false;

        ApplyBluntDamage(user, target, component, component.ConsecutiveCqcBluntDamage);
        _stamina.TakeStaminaDamage(target, component.ConsecutiveCqcStaminaDamage, source: user);
        TryPickupTargetActiveItem(user, target);
        return true;
    }

    private bool CanUseRestrainFollowup(Entity<CloseQuarterCombatMasteryComponent> ent, EntityUid target)
    {
        if (!ent.Comp.RestrainFollowupReady || ent.Comp.RestrainFollowupTarget != target)
            return false;

        if (_timing.CurTime <= ent.Comp.RestrainFollowupExpireAt)
            return true;

        ResetRestrainFollowup(ent.Comp);
        return false;
    }

    private void ExecuteRestrainFollowup(Entity<CloseQuarterCombatMasteryComponent> ent, EntityUid target)
    {
        _statusEffects.TryAddStatusEffectDuration(target,
            SleepingSystem.StatusEffectForcedSleeping,
            ent.Comp.RestrainFollowupSleepDuration);

        if (TryComp<PullableComponent>(target, out var pullable))
            _pulling.TogglePull((target, pullable), ent.Owner);

        if (_random.Prob(ent.Comp.RestrainFollowupBonusDamageChance))
        {
            ApplyBluntDamage(ent.Owner, target, ent.Comp, ent.Comp.RestrainFollowupBluntDamage);
            _jittering.DoJitter(target, ent.Comp.RestrainFollowupJitterDuration, refresh: true);
        }

        TryPickupTargetActiveItem(ent.Owner, target);
        PopupTechnique(ent.Owner, target,
            "cqc-followup-pressure-attacker-popup",
            "cqc-followup-pressure-target-popup",
            true);
        ResetRestrainFollowup(ent.Comp);
    }

    private void PopupTechnique(EntityUid user, EntityUid target, string attackerLocKey, string targetLocKey, bool includeTargetName = false)
    {
        var attackerMessage = includeTargetName
            ? Loc.GetString(attackerLocKey, ("target", Identity.Entity(target, EntityManager)))
            : Loc.GetString(attackerLocKey);
        var targetMessage = Loc.GetString(targetLocKey);

        _popup.PopupEntity(attackerMessage, user, user);
        _popup.PopupEntity(targetMessage, target, target);
    }

    private void TryPickupTargetActiveItem(EntityUid user, EntityUid target)
    {
        if (!TryComp<HandsComponent>(target, out var targetHands))
            return;

        if (!_hands.TryGetActiveItem((target, targetHands), out var item))
            return;

        if (HasComp<VirtualItemComponent>(item.Value))
            return;

        _hands.PickupOrDrop(user, item.Value, checkActionBlocker: false, animate: false, dropNear: true);
    }

    private bool HasRealActiveItem(EntityUid user)
    {
        return _hands.TryGetActiveItem(user, out var held) &&
               !HasComp<VirtualItemComponent>(held.Value);
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

    private bool IsEntityDown(EntityUid uid)
    {
        return _standing.IsDown(uid)
               || HasComp<KnockedDownComponent>(uid)
               || HasComp<SleepingComponent>(uid);
    }

    private bool IsTargetStunned(EntityUid target)
    {
        return HasComp<StunnedComponent>(target)
               || HasComp<KnockedDownComponent>(target)
               || HasComp<SleepingComponent>(target);
    }

    private static bool IsUnarmedMeleeAttack(AttackedEvent args)
    {
        return args.Used == args.User;
    }

    private static DamageSpecifier ScaleDamageToTotal(DamageSpecifier damage, float total)
    {
        if (total <= 0f || damage.Empty)
            return new DamageSpecifier();

        var currentTotal = damage.GetTotal();
        if (currentTotal <= FixedPoint2.Zero)
            return new DamageSpecifier();

        var desiredTotal = FixedPoint2.New(total);
        var multiplier = desiredTotal / currentTotal;

        var scaled = new DamageSpecifier();
        scaled.DamageDict.EnsureCapacity(damage.DamageDict.Count);
        foreach (var (type, value) in damage.DamageDict)
        {
            scaled.DamageDict[type] = value * multiplier;
        }

        return scaled;
    }

    private static void ResetRestrainFollowup(CloseQuarterCombatMasteryComponent component)
    {
        component.RestrainFollowupReady = false;
        component.SkipNextComboResetForRestrain = false;
        component.RestrainFollowupTarget = null;
        component.RestrainFollowupExpireAt = default;
    }

    private sealed class PendingMeleeDamageChange
    {
        public EntityUid? Origin;
        public TimeSpan ExpiresAt;
        public float? DesiredDamage;
        public bool Nullify;
    }
}
