using Content.Server._OpenSpace.Combat.CombatMastery.Components;
using Content.Server._OpenSpace.Combat.CombatMastery.Hud;
using Content.Shared.CombatMode;
using Content.Shared.Damage;
using Content.Shared.FixedPoint;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory.VirtualItem;
using Content.Shared.Interaction;
using Content.Shared.Interaction.Events;
using Content.Shared.Mobs.Components;
using Content.Shared.Movement.Pulling.Events;
using Content.Shared.Weapons.Melee;
using Content.Shared._OpenSpace.Combat.CombatMastery;
using Content.Shared._OpenSpace.Combat.CombatMastery.Hud.Components;

namespace Content.Server._OpenSpace.Combat.CombatMastery.Systems;

public sealed class CombatMasteryControllerSystem : EntitySystem
{
    [Dependency] private readonly SharedCombatModeSystem _combatMode = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<CombatMasteryComponent, ComponentInit>(OnCombatMasteryInit);
        SubscribeLocalEvent<CombatMasteryComponent, ComponentShutdown>(OnCombatMasteryShutdown);
        SubscribeLocalEvent<CombatMasteryComponent, UserInteractHandEvent>(OnUserInteractHand);
        SubscribeLocalEvent<CombatMasteryComponent, AttackAttemptEvent>(OnAttackAttempt);
        SubscribeLocalEvent<CombatMasteryComponent, CombatGrabPerformedEvent>(OnCombatGrabPerformed);
        SubscribeLocalEvent<CombatMasteryComponent, CombatMasteryHudRefreshEvent>(OnHudRefreshRequested);
        SubscribeLocalEvent<CombatMasteryComponent, CombatMasteryRefreshMeleeDamageEvent>(OnMeleeDamageRefreshRequested);
    }

    private void OnCombatMasteryInit(Entity<CombatMasteryComponent> ent, ref ComponentInit args)
    {
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
        if (args.Cancelled || args.Uid != ent.Owner || args.Target is not { } target)
            return;

        if (args.Disarm)
        {
            HandleDisarm(ent, target);
            return;
        }

        HandleAttack(ent, target);
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

        UpdateCombo(ent, target, ComboMasteryKeys.help);
    }

    private void HandleAttack(Entity<CombatMasteryComponent> ent, EntityUid target)
    {
        if (_hands.TryGetActiveItem(ent.Owner, out var held) &&
            !IsPullingTargetInActiveHand(held.Value, target))
        {
            ClearCombo(ent);
            return;
        }

        UpdateCombo(ent, target, ComboMasteryKeys.attack);
    }

    private bool IsPullingTargetInActiveHand(EntityUid held, EntityUid target) =>
        TryComp<VirtualItemComponent>(held, out var virtualItem) &&
        virtualItem.BlockingEntity == target;

    private bool HandleGrab(Entity<CombatMasteryComponent> ent, EntityUid target)
    {
        if (!_combatMode.IsInCombatMode(ent.Owner) || !HasComp<MobStateComponent>(target))
            return false;

        return UpdateCombo(ent, target, ComboMasteryKeys.grab);
    }

    private void HandleDisarm(Entity<CombatMasteryComponent> ent, EntityUid target)
    {
        if (!HasComp<MobStateComponent>(target))
            return;

        UpdateCombo(ent, target, ComboMasteryKeys.disarm);
    }

    private bool UpdateCombo(Entity<CombatMasteryComponent> ent, EntityUid target, ComboMasteryKeys key)
    {
        var component = ent.Comp;
        if (component.CurrentTarget != target)
        {
            component.CombatMasteryCurrentCombo.Clear();
            component.CurrentTarget = target;
        }
        else if (component.CombatMasteryCurrentCombo.Count >= Math.Max(1, component.MaxComboLength))
        {
            component.CombatMasteryCurrentCombo.RemoveAt(0);
        }

        component.CombatMasteryCurrentCombo.Add(key);
        var executed = CheckTemplates(ent);
        SyncHudState(ent);
        return executed;
    }

    private bool CheckTemplates(Entity<CombatMasteryComponent> ent)
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

    private void SyncHudState(Entity<CombatMasteryComponent> ent)
    {
        var hud = EnsureComp<CombatMasteryComboHudComponent>(ent.Owner);
        hud.HasActiveMastery = HasActiveMastery(ent.Owner);
        hud.VisibleCombo.Clear();

        if (hud.HasActiveMastery && ent.Comp.CombatMasteryCurrentCombo.Count > 0)
        {
            var startIndex = Math.Max(0, ent.Comp.CombatMasteryCurrentCombo.Count - CombatMasteryComboHudComponent.MaxVisibleKeys);
            for (var index = startIndex; index < ent.Comp.CombatMasteryCurrentCombo.Count; index++)
            {
                hud.VisibleCombo.Add(ent.Comp.CombatMasteryCurrentCombo[index]);
            }
        }

        Dirty(ent.Owner, hud);
    }
}
