using Content.Server._OpenSpace.Combat.CombatMastery.Components;
using Content.Server._OpenSpace.Combat.CombatMastery.Hud;
using Content.Shared.CombatMode;
using Content.Shared.Damage;
using Content.Shared.FixedPoint;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Interaction.Events;
using Content.Shared.Mobs.Components;
using Content.Shared.Movement.Pulling.Events;
using Content.Shared.Weapons.Melee;
using Content.Shared.Weapons.Melee.Events;
using Content.Shared._OpenSpace.Combat.CombatMastery;
using Content.Shared._OpenSpace.Combat.CombatMastery.Events;
using Content.Shared._OpenSpace.Combat.CombatMastery.Hud.Components;
using Robust.Shared.Timing;

namespace Content.Server._OpenSpace.Combat.CombatMastery.Systems;

public sealed class CombatMasteryControllerSystem : EntitySystem
{
    [Dependency] private readonly SharedCombatModeSystem _combatMode = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<CombatMasteryComponent, ComponentInit>(OnCombatMasteryInit);
        SubscribeLocalEvent<CombatMasteryComponent, ComponentShutdown>(OnCombatMasteryShutdown);
        SubscribeLocalEvent<CombatMasteryComponent, UserInteractHandEvent>(OnUserInteractHand);
        SubscribeLocalEvent<CombatMasteryComponent, AttackAttemptEvent>(OnAttackAttempt);
        SubscribeLocalEvent<CombatMasteryComponent, MeleeHitEvent>(OnMeleeHit);
        SubscribeLocalEvent<CombatMasteryComponent, CombatDisarmAttemptedEvent>(OnCombatDisarmAttempted);
        SubscribeLocalEvent<CombatMasteryComponent, CombatGrabPerformedEvent>(OnCombatGrabPerformed);
        SubscribeLocalEvent<CombatMasteryComponent, CombatMasteryHudRefreshEvent>(OnHudRefreshRequested);
        SubscribeLocalEvent<CombatMasteryComponent, CombatMasteryRefreshMeleeDamageEvent>(OnMeleeDamageRefreshRequested);
    }

    private void OnCombatMasteryInit(Entity<CombatMasteryComponent> ent, ref ComponentInit args)
    {
        ent.Comp.LastComboUpdateTime = null;
        ent.Comp.PendingMeleeDamageRefresh = false;
        ent.Comp.PendingHudStateRefresh = false;
        EnsureComp<CombatMasteryComboHudComponent>(ent.Owner);
        RefreshMeleeDamage(ent);
        SyncHudState(ent);
    }

    private void OnCombatMasteryShutdown(Entity<CombatMasteryComponent> ent, ref ComponentShutdown args)
    {
        RemComp<CombatMasteryComboHudComponent>(ent.Owner);
    }

    private void OnUserInteractHand(Entity<CombatMasteryComponent> ent, ref UserInteractHandEvent args)
    {
        if (_combatMode.IsInCombatMode(ent))
            return;

        HandleHelpInteraction(ent, args.Target);
    }

    private void OnAttackAttempt(Entity<CombatMasteryComponent> ent, ref AttackAttemptEvent args)
    {
        if (args.Cancelled || args.Uid != ent.Owner)
            return;

        if (args.Target is not { } target || target == ent.Owner)
        {
            ClearCombo(ent);
            return;
        }
    }

    private void OnCombatGrabPerformed(Entity<CombatMasteryComponent> ent, ref CombatGrabPerformedEvent args)
    {
        if (args.PullerUid != ent.Owner)
            return;

        args.SuppressPopup = HandleGrab(ent, args.TargetUid);
    }

    private void HandleHelpInteraction(Entity<CombatMasteryComponent> ent, EntityUid target)
    {
        if (_hands.TryGetActiveItem(ent.Owner, out _))
            return;

        UpdateComboAndTryExecute(ent, target, ComboMasteryKeys.help);
    }

    private void OnMeleeHit(Entity<CombatMasteryComponent> ent, ref MeleeHitEvent args)
    {
        if (!args.IsHit ||
            args.User != ent.Owner ||
            args.Direction != null)
        {
            return;
        }

        if (args.HitEntities.Count == 0)
        {
            ClearCombo(ent);
            return;
        }

        var target = args.HitEntities[0];
        if (!HasComp<MobStateComponent>(target))
        {
            ClearCombo(ent);
            return;
        }

        if (TryExecuteTechnique(ent, target, ComboMasteryKeys.attack))
        {
            args.Handled = true;
            return;
        }

        RecordCombo(ent, target, ComboMasteryKeys.attack);
    }

    private void OnCombatDisarmAttempted(Entity<CombatMasteryComponent> ent, ref CombatDisarmAttemptedEvent args)
    {
        if (args.User != ent.Owner)
            return;

        if (args.Target == ent.Owner)
        {
            ClearCombo(ent);
            return;
        }

        if (TryExecuteTechnique(ent, args.Target, ComboMasteryKeys.disarm))
        {
            args.Cancelled = true;
            return;
        }

        RecordCombo(ent, args.Target, ComboMasteryKeys.disarm);
    }

    private bool HandleGrab(Entity<CombatMasteryComponent> ent, EntityUid target)
    {
        if (!_combatMode.IsInCombatMode(ent.Owner) || !HasComp<MobStateComponent>(target))
            return false;

        return UpdateComboAndTryExecute(ent, target, ComboMasteryKeys.grab);
    }

    private bool UpdateComboAndTryExecute(Entity<CombatMasteryComponent> ent, EntityUid target, ComboMasteryKeys key)
    {
        if (!TryPrepareComboUpdate(ent, target))
            return false;

        ent.Comp.CombatMasteryCurrentCombo.Add(key);
        var executed = TryExecuteMatchedTemplate(ent);
        RefreshComboTimestamp(ent.Comp);
        SyncHudState(ent);
        return executed;
    }

    private void RecordCombo(Entity<CombatMasteryComponent> ent, EntityUid target, ComboMasteryKeys key)
    {
        if (!TryPrepareComboUpdate(ent, target))
            return;

        ent.Comp.CombatMasteryCurrentCombo.Add(key);
        RefreshComboTimestamp(ent.Comp);
        SyncHudState(ent);
    }

    private bool TryPrepareComboUpdate(Entity<CombatMasteryComponent> ent, EntityUid target)
    {
        if (target == ent.Owner)
        {
            ClearCombo(ent);
            return false;
        }

        ResetComboForNewTarget(ent.Comp, target);
        TrimCombo(ent.Comp);
        return true;
    }

    private bool TryExecuteTechnique(Entity<CombatMasteryComponent> ent, EntityUid target, ComboMasteryKeys key)
    {
        if (target == ent.Owner)
        {
            ClearCombo(ent);
            return false;
        }

        var previewCombo = BuildPreviewCombo(ent.Comp, target, key);
        var ev = new CombatMasteryComboUpdatedEvent(target, previewCombo);
        RaiseLocalEvent(ent.Owner, ref ev);

        if (!ev.TemplateExecuted)
            return false;

        ent.Comp.CurrentTarget = target;
        ent.Comp.CombatMasteryCurrentCombo.Clear();
        RefreshComboTimestamp(ent.Comp);
        SyncHudState(ent);
        return true;
    }

    private static List<ComboMasteryKeys> BuildPreviewCombo(
        CombatMasteryComponent component,
        EntityUid target,
        ComboMasteryKeys key)
    {
        var combo = component.CurrentTarget == target
            ? new List<ComboMasteryKeys>(component.CombatMasteryCurrentCombo)
            : new List<ComboMasteryKeys>();

        if (combo.Count >= Math.Max(1, component.MaxComboLength))
            combo.RemoveAt(0);

        combo.Add(key);
        return combo;
    }

    private bool TryExecuteMatchedTemplate(Entity<CombatMasteryComponent> ent)
    {
        if (ent.Comp.CurrentTarget is not { } target || ent.Comp.CombatMasteryCurrentCombo.Count == 0)
            return false;

        var ev = new CombatMasteryComboUpdatedEvent(target, ent.Comp.CombatMasteryCurrentCombo);
        RaiseLocalEvent(ent.Owner, ref ev);

        if (ev.TemplateExecuted)
            ent.Comp.CombatMasteryCurrentCombo.Clear();

        return ev.TemplateExecuted;
    }

    private void ClearCombo(Entity<CombatMasteryComponent> ent)
    {
        ent.Comp.CombatMasteryCurrentCombo.Clear();
        ent.Comp.CurrentTarget = null;
        ent.Comp.LastComboUpdateTime = null;
        SyncHudState(ent);
    }

    private void OnHudRefreshRequested(Entity<CombatMasteryComponent> ent, ref CombatMasteryHudRefreshEvent args)
    {
        ent.Comp.PendingHudStateRefresh = true;
    }

    private void OnMeleeDamageRefreshRequested(Entity<CombatMasteryComponent> ent, ref CombatMasteryRefreshMeleeDamageEvent args)
    {
        ent.Comp.PendingMeleeDamageRefresh = true;
    }

    private void RefreshMeleeDamage(Entity<CombatMasteryComponent> ent)
    {
        if (!TryComp<MeleeWeaponComponent>(ent.Owner, out var melee))
            return;

        if (ent.Comp.OriginalUnarmedMeleeDamage == null)
            ent.Comp.OriginalUnarmedMeleeDamage = new DamageSpecifier(melee.Damage);

        var originalDamage = ent.Comp.OriginalUnarmedMeleeDamage;
        if (originalDamage == null)
            return;

        var originalTotal = originalDamage.GetTotal().Float();
        var collectEvent = new CombatMasteryCollectMeleeDamageEvent(originalTotal);
        RaiseLocalEvent(ent.Owner, ref collectEvent);

        var desiredDamage = ScaleDamageToTotal(originalDamage, collectEvent.HighestDamage);
        melee.Damage = desiredDamage;
        Dirty(ent.Owner, melee);
    }

    private static DamageSpecifier ScaleDamageToTotal(DamageSpecifier sourceDamage, float total)
    {
        if (sourceDamage.Empty || total <= 0f)
            return new DamageSpecifier();

        var sourceTotal = sourceDamage.GetTotal();
        if (sourceTotal <= FixedPoint2.Zero)
            return new DamageSpecifier(sourceDamage);

        var desiredTotal = FixedPoint2.New(total);
        var multiplier = desiredTotal / sourceTotal;

        var scaled = new DamageSpecifier();
        scaled.DamageDict.EnsureCapacity(sourceDamage.DamageDict.Count);

        foreach (var (type, value) in sourceDamage.DamageDict)
        {
            scaled.DamageDict[type] = value * multiplier;
        }

        return scaled;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<CombatMasteryComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            if (IsComboExpired(comp))
            {
                ClearCombo((uid, comp));
                continue;
            }

            if (comp.PendingHudStateRefresh)
            {
                comp.PendingHudStateRefresh = false;
                SyncHudState((uid, comp));
            }

            if (comp.PendingMeleeDamageRefresh)
            {
                comp.PendingMeleeDamageRefresh = false;
                RefreshMeleeDamage((uid, comp));
            }
        }
    }

    private bool HasActiveMastery(EntityUid uid)
    {
        var query = new CombatMasteryActiveStateQueryEvent();
        RaiseLocalEvent(uid, ref query);
        return query.HasActiveMastery;
    }

    private bool IsComboExpired(CombatMasteryComponent component)
    {
        if (component.CombatMasteryCurrentCombo.Count == 0)
            return false;

        return component.LastComboUpdateTime == null ||
               _timing.CurTime - component.LastComboUpdateTime.Value >= component.ComboTimeout;
    }

    private void RefreshComboTimestamp(CombatMasteryComponent component)
    {
        component.LastComboUpdateTime = component.CombatMasteryCurrentCombo.Count > 0
            ? _timing.CurTime
            : null;
    }

    private void SyncHudState(Entity<CombatMasteryComponent> ent)
    {
        var hud = EnsureComp<CombatMasteryComboHudComponent>(ent.Owner);
        hud.HasActiveMastery = HasActiveMastery(ent.Owner);
        hud.VisibleCombo.Clear();

        if (hud.HasActiveMastery && ent.Comp.CombatMasteryCurrentCombo.Count > 0)
            CopyVisibleCombo(ent.Comp, hud);

        Dirty(ent.Owner, hud);
    }

    private static void ResetComboForNewTarget(CombatMasteryComponent component, EntityUid target)
    {
        if (component.CurrentTarget == target)
            return;

        component.CombatMasteryCurrentCombo.Clear();
        component.CurrentTarget = target;
    }

    private static void TrimCombo(CombatMasteryComponent component)
    {
        if (component.CombatMasteryCurrentCombo.Count < Math.Max(1, component.MaxComboLength))
            return;

        component.CombatMasteryCurrentCombo.RemoveAt(0);
    }

    private static void CopyVisibleCombo(CombatMasteryComponent mastery, CombatMasteryComboHudComponent hud)
    {
        var startIndex = Math.Max(0, mastery.CombatMasteryCurrentCombo.Count - CombatMasteryComboHudComponent.MaxVisibleKeys);
        for (var index = startIndex; index < mastery.CombatMasteryCurrentCombo.Count; index++)
        {
            hud.VisibleCombo.Add(mastery.CombatMasteryCurrentCombo[index]);
        }
    }
}
