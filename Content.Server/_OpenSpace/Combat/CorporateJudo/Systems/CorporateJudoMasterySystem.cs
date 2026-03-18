using Content.Server._OpenSpace.Combat.CombatMastery;
using Content.Server._OpenSpace.Combat.CombatMastery.Components;
using Content.Server._OpenSpace.Combat.CombatMastery.Systems;
using Content.Server._OpenSpace.Combat.CorporateJudo.Components;
using Content.Shared.Bed.Sleep;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Systems;
using Content.Shared.Eye.Blinding.Components;
using Content.Shared.Eye.Blinding.Systems;
using Content.Shared.FixedPoint;
using Content.Shared.Flash.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.IdentityManagement;
using Content.Shared.Movement.Pulling.Components;
using Content.Shared.Movement.Systems;
using Content.Shared.Popups;
using Content.Shared.Standing;
using Content.Shared.StatusEffectNew;
using Content.Shared.Stunnable;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using LegacyStatusEffectsSystem = Content.Shared.StatusEffect.StatusEffectsSystem;

namespace Content.Server._OpenSpace.Combat.CorporateJudo.Systems;

public sealed class CorporateJudoMasterySystem : CombatMasteryTemplateCollectionSystem<CorporateJudoMasteryComponent>
{
    [Dependency] private readonly BlindableSystem _blindable = default!;
    [Dependency] private readonly DamageableSystem _damageable = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly LegacyStatusEffectsSystem _legacyStatusEffects = default!;
    [Dependency] private readonly MovementModStatusSystem _movement = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly StandingStateSystem _standing = default!;
    [Dependency] private readonly SharedStaminaSystem _stamina = default!;
    [Dependency] private readonly StatusEffectsSystem _statusEffects = default!;
    [Dependency] private readonly SharedStunSystem _stun = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<CorporateJudoMasteryComponent, ComponentStartup>(OnStarted);
        SubscribeLocalEvent<CorporateJudoMasteryComponent, ComponentRemove>(OnRemoved);
        SubscribeLocalEvent<CorporateJudoMasteryComponent, CombatMasteryCollectMeleeDamageEvent>(OnCollectMeleeDamage);
    }

    private void OnStarted(EntityUid uid, CorporateJudoMasteryComponent component, ComponentStartup args)
    {
        RequestMeleeDamageRefresh(uid);
    }

    private void OnRemoved(EntityUid uid, CorporateJudoMasteryComponent component, ComponentRemove args)
    {
        RequestMeleeDamageRefresh(uid);
    }

    private static void OnCollectMeleeDamage(Entity<CorporateJudoMasteryComponent> ent, ref CombatMasteryCollectMeleeDamageEvent args)
    {
        args.ConsiderDamage(ent.Comp.UnarmedDamage);
    }

    protected override void OnTemplateMatched(Entity<CorporateJudoMasteryComponent> ent, EntityUid target, CombatMasteryTemplate template)
    {
        switch (template.Name)
        {
            case CorporateJudoMasteryComponent.DiscombobulateTemplateName:
                if (DoDiscombobulate(ent.Owner, target, ent.Comp))
                {
                    PopupTechnique(ent.Owner, target,
                        "corporate-judo-discombobulate-attacker-popup",
                        "corporate-judo-discombobulate-target-popup");
                }
                break;
            case CorporateJudoMasteryComponent.EyePokeTemplateName:
                if (DoEyePoke(ent.Owner, target, ent.Comp))
                {
                    PopupTechnique(ent.Owner, target,
                        "corporate-judo-eye-poke-attacker-popup",
                        "corporate-judo-eye-poke-target-popup");
                }
                break;
            case CorporateJudoMasteryComponent.JudoThrowTemplateName:
                if (DoJudoThrow(ent.Owner, target, ent.Comp))
                {
                    PopupTechnique(ent.Owner, target,
                        "corporate-judo-throw-attacker-popup",
                        "corporate-judo-throw-target-popup");
                }
                break;
            case CorporateJudoMasteryComponent.ArmbarTemplateName:
                if (DoArmbar(ent.Owner, target, ent.Comp))
                {
                    PopupTechnique(ent.Owner, target,
                        "corporate-judo-armbar-attacker-popup",
                        "corporate-judo-armbar-target-popup");
                }
                break;
            case CorporateJudoMasteryComponent.WheelThrowTemplateName:
                if (DoWheelThrow(ent.Owner, target, ent.Comp))
                {
                    PopupTechnique(ent.Owner, target,
                        "corporate-judo-wheel-throw-attacker-popup",
                        "corporate-judo-wheel-throw-target-popup");
                }
                break;
            case CorporateJudoMasteryComponent.GoldenBlastTemplateName:
                if (DoGoldenBlast(ent.Owner, target, ent.Comp))
                {
                    PopupTechnique(ent.Owner, target,
                        "corporate-judo-golden-blast-attacker-popup",
                        "corporate-judo-golden-blast-target-popup");
                }
                break;
        }
    }

    private bool DoDiscombobulate(EntityUid user, EntityUid target, CorporateJudoMasteryComponent component)
    {
        if (TerminatingOrDeleted(target))
            return false;

        ApplyDisorient(target, component, component.DiscombobulateDisorientDuration);
        _stamina.TakeStaminaDamage(target, component.DiscombobulateStaminaDamage, source: user);
        return true;
    }

    private bool DoEyePoke(EntityUid user, EntityUid target, CorporateJudoMasteryComponent component)
    {
        if (TerminatingOrDeleted(target))
            return false;

        _statusEffects.TryAddStatusEffectDuration(target,
            CorporateJudoMasteryComponent.TemporaryBlindnessStatusEffectId,
            component.EyePokeBlindDuration);

        ApplyEyePokeBlur(target, component);
        ApplyBluntDamage(user, target, component, component.EyePokeBluntDamage);
        return true;
    }

    private bool DoJudoThrow(EntityUid user, EntityUid target, CorporateJudoMasteryComponent component)
    {
        if (TerminatingOrDeleted(target))
            return false;

        _stamina.TakeStaminaDamage(target, component.JudoThrowStaminaDamage, source: user);
        ApplySlipLikeStun(target, component.JudoThrowKnockdownDuration);
        return true;
    }

    private bool DoArmbar(EntityUid user, EntityUid target, CorporateJudoMasteryComponent component)
    {
        if (TerminatingOrDeleted(target) || !IsEntityDown(target))
            return false;

        _stamina.TakeStaminaDamage(target, component.ArmbarStaminaDamage, source: user);
        ApplySlipLikeStun(target, component.ArmbarStunDuration);
        return true;
    }

    private bool DoWheelThrow(EntityUid user, EntityUid target, CorporateJudoMasteryComponent component)
    {
        if (TerminatingOrDeleted(target) || !CanUseWheelThrow(user, target))
            return false;

        _stamina.TakeStaminaDamage(target, component.WheelThrowStaminaDamage, source: user);
        ApplySlipLikeStun(target, component.WheelThrowStunDuration);
        ApplyDisorient(target, component, component.WheelThrowDisorientDuration);
        return true;
    }

    private bool DoGoldenBlast(EntityUid user, EntityUid target, CorporateJudoMasteryComponent component)
    {
        if (TerminatingOrDeleted(target))
            return false;

        ApplySlipLikeStun(target, component.GoldenBlastStunDuration);
        ApplyDisorient(target, component, component.GoldenBlastDisorientDuration);
        return true;
    }

    private bool CanUseWheelThrow(EntityUid user, EntityUid target)
    {
        if (_hands.TryGetActiveItem(user, out _))
            return false;

        if (!TryComp<PullerComponent>(user, out var puller) ||
            puller.Pulling != target ||
            puller.GrabStage == GrabStage.None)
        {
            return false;
        }

        return TryComp<CombatMasteryComponent>(user, out var mastery) &&
               mastery.CurrentTarget == target;
    }

    private void ApplyDisorient(EntityUid target, CorporateJudoMasteryComponent component, TimeSpan duration)
    {
        EnsureComp<Content.Shared.StatusEffect.StatusEffectsComponent>(target);
        _legacyStatusEffects.TryAddStatusEffect<FlashedComponent>(
            target,
            CorporateJudoMasteryComponent.FlashedStatusEffectId,
            duration,
            refresh: false);
        _movement.TryAddMovementSpeedModDuration(target, MovementModStatusSystem.FlashSlowdown, duration, component.FlashSlowTo);
    }

    private void ApplyEyePokeBlur(EntityUid target, CorporateJudoMasteryComponent component)
    {
        if (!TryComp<BlindableComponent>(target, out var blindable))
            return;

        var blur = EnsureComp<CorporateJudoEyePokeBlurComponent>(target);
        var now = _timing.CurTime;
        var remaining = blur.ExpireAt > now
            ? blur.ExpireAt - now
            : TimeSpan.Zero;
        var totalDuration = remaining + component.EyePokeBlurDuration;
        if (totalDuration > component.EyePokeBlurMaximumDuration)
            totalDuration = component.EyePokeBlurMaximumDuration;

        blur.ExpireAt = now + totalDuration;

        if (!blur.Initialized)
        {
            blur.Initialized = true;
            blur.OriginalMinEyeDamage = blindable.MinDamage;
            blur.AppliedMinEyeDamage = Math.Max(blur.OriginalMinEyeDamage, component.EyePokeBlurMinEyeDamage);
        }

        if (blindable.MinDamage < blur.AppliedMinEyeDamage)
            _blindable.SetMinDamage((target, blindable), blur.AppliedMinEyeDamage);
    }

    private void ApplySlipLikeStun(EntityUid target, TimeSpan duration)
    {
        _stun.TryUpdateStunDuration(target, duration);
        _stun.TryKnockdown(target,
            duration,
            refresh: true,
            autoStand: true,
            drop: true,
            force: true,
            voluntary: false);
    }

    private void ApplyBluntDamage(EntityUid user, EntityUid target, CorporateJudoMasteryComponent component, float amount)
    {
        if (!TryComp<DamageableComponent>(target, out _))
            return;

        var bluntDamage = new DamageSpecifier(_prototypeManager.Index(component.BluntDamageType), FixedPoint2.New(amount));
        _damageable.TryChangeDamage(target, bluntDamage, ignoreResistances: true, origin: user);
    }

    private void PopupTechnique(EntityUid user, EntityUid target, string attackerLocKey, string targetLocKey)
    {
        var attackerMessage = Loc.GetString(attackerLocKey, ("target", Identity.Entity(target, EntityManager)));
        var targetMessage = Loc.GetString(targetLocKey);

        _popup.PopupEntity(attackerMessage, user, user);
        _popup.PopupEntity(targetMessage, target, target);
    }

    private bool IsEntityDown(EntityUid uid)
    {
        return _standing.IsDown(uid)
               || HasComp<KnockedDownComponent>(uid)
               || HasComp<SleepingComponent>(uid);
    }

    private void RequestMeleeDamageRefresh(EntityUid uid)
    {
        var ev = new CombatMasteryRefreshMeleeDamageEvent();
        RaiseLocalEvent(uid, ref ev);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<CorporateJudoEyePokeBlurComponent>();
        while (query.MoveNext(out var uid, out var blur))
        {
            if (now < blur.ExpireAt)
                continue;

            if (blur.Initialized &&
                TryComp<BlindableComponent>(uid, out var blindable) &&
                blindable.MinDamage == blur.AppliedMinEyeDamage)
            {
                _blindable.SetMinDamage((uid, blindable), blur.OriginalMinEyeDamage);
            }

            RemCompDeferred<CorporateJudoEyePokeBlurComponent>(uid);
        }
    }
}
