using System.Numerics;
using Content.Server._OpenSpace.Combat.CloseQuarterCombatMastery.Components;
using Content.Server._OpenSpace.Combat.CombatMastery;
using Content.Server._OpenSpace.Combat.CombatMastery.Systems;
using Content.Shared.Bed.Sleep;
using Content.Shared.CombatMode;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Events;
using Content.Shared.Damage.Systems;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction.Events;
using Content.Shared.Inventory.VirtualItem;
using Content.Shared.Jittering;
using Content.Shared.Movement.Pulling.Components;
using Content.Shared.Movement.Pulling.Systems;
using Content.Shared.StatusEffectNew;
using Content.Shared.Stunnable;
using Content.Shared.Throwing;
using Content.Shared.Weapons.Melee;
using Content.Shared.Weapons.Melee.Events;
using Content.Shared._OpenSpace.Combat.CombatMastery;
using Content.Shared._Starlight.Medical.Damage;
using Robust.Server.GameObjects;
using Robust.Shared.Maths;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._OpenSpace.Combat.CloseQuarterCombatMastery.Systems;

public sealed class CloseQuarterCombatMasterySystem : CombatMasteryTechniqueSystem<CloseQuarterCombatMasteryComponent>
{
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly SharedJitteringSystem _jittering = default!;
    [Dependency] private readonly PullingSystem _pulling = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly StatusEffectsSystem _statusEffects = default!;
    [Dependency] private readonly SharedStaminaSystem _stamina = default!;
    [Dependency] private readonly SharedStunSystem _stun = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly ThrowingSystem _throwing = default!;
    [Dependency] private readonly TransformSystem _transform = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<CloseQuarterCombatMasteryComponent, AttackAttemptEvent>(OnAttackAttempt,
            before: [typeof(CombatMasteryControllerSystem)]);
        SubscribeLocalEvent<MeleeWeaponComponent, MeleeHitEvent>(OnMeleeHit, before: [typeof(SharedStaminaSystem)]);
        SubscribeLocalEvent<CloseQuarterCombatMasteryComponent, DisarmedEvent>(OnDisarmed, before: [typeof(SharedStaminaSystem)]);
        SubscribeLocalEvent<CloseQuarterCombatMasteryComponent, BeforeStaminaDamageEvent>(OnBeforeStaminaDamage);
        SubscribeLocalEvent<CloseQuarterCombatMasteryComponent, CombatMasteryCollectMeleeDamageEvent>(OnCollectMeleeDamage);
        SubscribeLocalEvent<DamageableComponent, AttackedEvent>(OnMeleeAttacked);
        SubscribeLocalEvent<DamageableComponent, DamageBeforeApplyEvent>(OnDamageBeforeApply);
    }

    protected override void OnMasteryStarted(Entity<CloseQuarterCombatMasteryComponent> ent, ref ComponentStartup args)
    {
        RequestMeleeDamageRefresh(ent.Owner);
    }

    protected override void OnMasteryStopped(Entity<CloseQuarterCombatMasteryComponent> ent, ref ComponentShutdown args)
    {
        RequestMeleeDamageRefresh(ent.Owner);
    }

    private static void OnCollectMeleeDamage(Entity<CloseQuarterCombatMasteryComponent> ent, ref CombatMasteryCollectMeleeDamageEvent args)
    {
        args.ConsiderDamage(ent.Comp.UnarmedDamage);
    }

    protected override bool OnTemplateMatched(Entity<CloseQuarterCombatMasteryComponent> ent, EntityUid target, CombatMasteryTemplate template)
    {
        switch (template.Name)
        {
            case CloseQuarterCombatMasteryComponent.SlamTemplateName:
                return TryExecuteSlam(ent, target);
            case CloseQuarterCombatMasteryComponent.CQCKickTemplateName:
                return TryExecuteKick(ent, target);
            case CloseQuarterCombatMasteryComponent.RestrainTemplateName:
                return TryExecuteRestrain(ent, target);
            case CloseQuarterCombatMasteryComponent.PressureTemplateName:
                return TryExecutePressure(ent, target);
            case CloseQuarterCombatMasteryComponent.ConsecutiveCQCTemplateName:
                return TryExecuteConsecutiveCqc(ent, target);
        }

        return false;
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

    protected override void OnComboUpdated(Entity<CloseQuarterCombatMasteryComponent> ent, ref CombatMasteryComboUpdatedEvent args)
    {
        base.OnComboUpdated(ent, ref args);

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
        if (TryComp<CloseQuarterCombatMasteryComponent>(args.User, out var attackerCqc) &&
            IsUnarmedMeleeAttack(args))
        {
            var targetDown = IsEntityDown(ent.Owner);
            var bonusDamage = 0f;

            if (targetDown)
                bonusDamage += attackerCqc.UnarmedDownedTargetBonusDamage;

            if (IsEntityDown(args.User) && !targetDown)
            {
                bonusDamage += attackerCqc.ProneAttackerBonusDamage;
                _stun.TryKnockdown(ent.Owner,
                    attackerCqc.ProneAttackerKnockdownDuration,
                    refresh: true,
                    autoStand: true,
                    drop: true,
                    voluntary: true);
                StandImmediately(args.User);
            }

            if (bonusDamage > 0f)
                AddUnarmedBonusDamage(ref args, attackerCqc, bonusDamage);
        }
    }

    private void OnMeleeHit(Entity<MeleeWeaponComponent> ent, ref MeleeHitEvent args)
    {
        if (!args.IsHit || args.HitEntities.Count == 0)
            return;

        foreach (var target in args.HitEntities)
        {
            if (target == args.User)
                continue;

            if (!TryComp<CloseQuarterCombatMasteryComponent>(target, out var defenderCqc))
                continue;

            if (!_random.Prob(defenderCqc.DefensiveMeleeNullifyChance))
                continue;

            SetPendingDefensiveNullify(defenderCqc,
                args.User,
                nullifyDamage: true,
                nullifyStamina: true);

            PopupDefensiveNullify(target, args.User);
            ApplyDefensiveCounter(defenderCqc, args.User);
        }
    }

    private void OnDisarmed(Entity<CloseQuarterCombatMasteryComponent> ent, ref DisarmedEvent args)
    {
        if (args.Handled || !_random.Prob(ent.Comp.DefensiveMeleeNullifyChance))
            return;

        SetPendingDefensiveNullify(ent.Comp,
            args.Source,
            nullifyDamage: false,
            nullifyStamina: true);

        PopupDefensiveNullify(ent.Owner, args.Source);
        ApplyDefensiveCounter(ent.Comp, args.Source);
    }

    private void OnBeforeStaminaDamage(Entity<CloseQuarterCombatMasteryComponent> ent, ref BeforeStaminaDamageEvent args)
    {
        if (!ent.Comp.PendingDefensiveMeleeNullifyStamina)
            return;

        if (IsPendingDefensiveNullifyExpired(ent.Comp))
        {
            ResetPendingDefensiveNullify(ent.Comp);
            return;
        }

        args.Cancelled = true;
        ent.Comp.PendingDefensiveMeleeNullifyStamina = false;
        ClearPendingDefensiveNullifyIfUnused(ent.Comp);
    }

    private void OnDamageBeforeApply(Entity<DamageableComponent> ent, ref DamageBeforeApplyEvent args)
    {
        if (!TryComp<CloseQuarterCombatMasteryComponent>(ent.Owner, out var cqc) ||
            !cqc.PendingDefensiveMeleeNullify)
        {
            return;
        }

        if (IsPendingDefensiveNullifyExpired(cqc))
        {
            ResetPendingDefensiveNullify(cqc);
            return;
        }

        if (args.Origin != cqc.PendingDefensiveMeleeOrigin)
            return;

        args.Damage = new DamageSpecifier();

        cqc.PendingDefensiveMeleeNullify = false;
        ClearPendingDefensiveNullifyIfUnused(cqc);
    }

    private bool DoSlam(EntityUid user, EntityUid target, CloseQuarterCombatMasteryComponent component)
    {
        if (TerminatingOrDeleted(target) || IsTargetStunned(target))
            return false;

        ApplyBluntDamage(user, target, component.BluntDamageType, component.SlamBluntDamage);
        _stun.TryKnockdown(target, component.SlamKnockdownDuration, refresh: true, autoStand: true, drop: true, force: true);
        return true;
    }

    private bool DoCQCKick(EntityUid user, EntityUid target, CloseQuarterCombatMasteryComponent component)
    {
        if (TerminatingOrDeleted(target))
            return false;

        if (IsTargetStunned(target))
        {
            ApplyBluntDamage(user, target, component.BluntDamageType, component.CQCKickStunnedBluntDamage);
            _statusEffects.TryAddStatusEffectDuration(target, SleepingSystem.StatusEffectForcedSleeping, component.CQCKickSleepDuration);
            return true;
        }

        ApplyBluntDamage(user, target, component.BluntDamageType, component.CQCKickBluntDamage);
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

        ApplyBluntDamage(user, target, component.BluntDamageType, component.ConsecutiveCqcBluntDamage);
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
            ApplyBluntDamage(ent.Owner, target, ent.Comp.BluntDamageType, ent.Comp.RestrainFollowupBluntDamage);
            _jittering.DoJitter(target, ent.Comp.RestrainFollowupJitterDuration, refresh: true);
        }

        TryPickupTargetActiveItem(ent.Owner, target);
        PopupTechnique(ent.Owner, target,
            "cqc-followup-pressure-attacker-popup",
            "cqc-followup-pressure-target-popup",
            true);
        ResetRestrainFollowup(ent.Comp);
    }

    private void PopupDefensiveNullify(EntityUid defender, EntityUid attacker)
    {
        PopupTechnique(defender, attacker,
            "cqc-defensive-nullify-defender-popup",
            "cqc-defensive-nullify-attacker-popup");
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

    private void AddUnarmedBonusDamage(ref AttackedEvent args, CloseQuarterCombatMasteryComponent component, float bonusDamage)
    {
        args.BonusDamage += CreateBluntDamage(component.BluntDamageType, bonusDamage);
    }

    private static void ResetRestrainFollowup(CloseQuarterCombatMasteryComponent component)
    {
        component.RestrainFollowupReady = false;
        component.SkipNextComboResetForRestrain = false;
        component.RestrainFollowupTarget = null;
        component.RestrainFollowupExpireAt = default;
    }

    private static void ResetPendingDefensiveNullify(CloseQuarterCombatMasteryComponent component)
    {
        component.PendingDefensiveMeleeNullify = false;
        component.PendingDefensiveMeleeNullifyStamina = false;
        component.PendingDefensiveMeleeOrigin = null;
        component.PendingDefensiveMeleeExpireAt = default;
    }

    private void SetPendingDefensiveNullify(
        CloseQuarterCombatMasteryComponent component,
        EntityUid origin,
        bool nullifyDamage,
        bool nullifyStamina)
    {
        component.PendingDefensiveMeleeOrigin = origin;
        component.PendingDefensiveMeleeExpireAt = _timing.CurTime + component.DefensiveMeleeNullifyWindow;
        component.PendingDefensiveMeleeNullify = nullifyDamage;
        component.PendingDefensiveMeleeNullifyStamina = nullifyStamina;
    }

    private void ApplyDefensiveCounter(CloseQuarterCombatMasteryComponent component, EntityUid attacker)
    {
        if (!HasRealActiveItem(attacker))
            return;

        _stun.TryUpdateStunDuration(attacker, component.DefensiveMeleeCounterKnockdownDuration);
        _stun.TryKnockdown(attacker,
            component.DefensiveMeleeCounterKnockdownDuration,
            refresh: true,
            autoStand: true,
            drop: true,
            force: true);
    }

    private bool IsPendingDefensiveNullifyExpired(CloseQuarterCombatMasteryComponent component)
    {
        return _timing.CurTime > component.PendingDefensiveMeleeExpireAt;
    }

    private static void ClearPendingDefensiveNullifyIfUnused(CloseQuarterCombatMasteryComponent component)
    {
        if (component.PendingDefensiveMeleeNullify || component.PendingDefensiveMeleeNullifyStamina)
            return;

        component.PendingDefensiveMeleeOrigin = null;
        component.PendingDefensiveMeleeExpireAt = default;
    }
    private bool TryExecuteSlam(Entity<CloseQuarterCombatMasteryComponent> ent, EntityUid target)
    {
        if (!DoSlam(ent.Owner, target, ent.Comp))
            return false;

        PopupTechnique(ent.Owner, target, "cqc-slam-attacker-popup", "cqc-slam-target-popup", includeTargetName: true);
        return true;
    }

    private bool TryExecuteKick(Entity<CloseQuarterCombatMasteryComponent> ent, EntityUid target)
    {
        if (!DoCQCKick(ent.Owner, target, ent.Comp))
            return false;

        PopupTechnique(ent.Owner, target, "cqc-kick-attacker-popup", "cqc-kick-target-popup");
        return true;
    }

    private bool TryExecuteRestrain(Entity<CloseQuarterCombatMasteryComponent> ent, EntityUid target)
    {
        if (!DoRestrain(ent.Owner, target, ent.Comp))
            return false;

        PopupTechnique(ent.Owner, target, "cqc-restrain-attacker-popup", "cqc-restrain-target-popup", includeTargetName: true);
        return true;
    }

    private bool TryExecutePressure(Entity<CloseQuarterCombatMasteryComponent> ent, EntityUid target)
    {
        if (!DoPressure(ent.Owner, target, ent.Comp))
            return false;

        PopupTechnique(ent.Owner, target, "cqc-pressure-attacker-popup", "cqc-pressure-target-popup");
        return true;
    }

    private bool TryExecuteConsecutiveCqc(Entity<CloseQuarterCombatMasteryComponent> ent, EntityUid target)
    {
        if (!DoConsecutiveCqc(ent.Owner, target, ent.Comp))
            return false;

        PopupTechnique(ent.Owner, target, "cqc-consecutive-attacker-popup", "cqc-consecutive-target-popup", includeTargetName: true);
        return true;
    }
}
