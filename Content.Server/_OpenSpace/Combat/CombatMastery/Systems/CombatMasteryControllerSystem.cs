using Content.Server._OpenSpace.Combat.CombatMastery.Components;
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

namespace Content.Server._OpenSpace.Combat.CombatMastery.Systems;

public sealed class CombatMasteryControllerSystem : EntitySystem
{
    [Dependency] private readonly SharedCombatModeSystem _combatMode = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<CombatMasteryComponent, ComponentInit>(OnCombatMasteryInit);
        SubscribeLocalEvent<CombatMasteryComponent, UserInteractHandEvent>(OnUserInteractHand);
        SubscribeLocalEvent<CombatMasteryComponent, AttackAttemptEvent>(OnAttackAttempt);
        SubscribeLocalEvent<CombatMasteryComponent, PullStartedMessage>(OnPullStarted);
        SubscribeLocalEvent<CombatMasteryComponent, CombatMasteryRefreshMeleeDamageEvent>(OnMeleeDamageRefreshRequested);
    }

    private void OnCombatMasteryInit(Entity<CombatMasteryComponent> ent, ref ComponentInit args)
    {
        ent.Comp.PendingMeleeDamageRefresh = false;
        RefreshMeleeDamage(ent);
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

    private void OnPullStarted(Entity<CombatMasteryComponent> ent, ref PullStartedMessage args)
    {
        if (args.PullerUid != ent.Owner)
            return;

        HandleGrab(ent, args.PulledUid);
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
            ClearCombo(ent.Comp);
            return;
        }

        UpdateCombo(ent, target, ComboMasteryKeys.attack);
    }

    private bool IsPullingTargetInActiveHand(EntityUid held, EntityUid target) =>
        TryComp<VirtualItemComponent>(held, out var virtualItem) &&
        virtualItem.BlockingEntity == target;

    private void HandleGrab(Entity<CombatMasteryComponent> ent, EntityUid target)
    {
        if (!_combatMode.IsInCombatMode(ent.Owner) || !HasComp<MobStateComponent>(target))
            return;

        UpdateCombo(ent, target, ComboMasteryKeys.grab);
    }

    private void HandleDisarm(Entity<CombatMasteryComponent> ent, EntityUid target)
    {
        if (!HasComp<MobStateComponent>(target))
            return;

        UpdateCombo(ent, target, ComboMasteryKeys.disarm);
    }

    private void UpdateCombo(Entity<CombatMasteryComponent> ent, EntityUid target, ComboMasteryKeys key)
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
        CheckTemplates(ent);
    }

    private void CheckTemplates(Entity<CombatMasteryComponent> ent)
    {
        if (ent.Comp.CurrentTarget is not { } target || ent.Comp.CombatMasteryCurrentCombo.Count == 0)
            return;

        var ev = new CombatMasteryComboUpdatedEvent(target, ent.Comp.CombatMasteryCurrentCombo);
        RaiseLocalEvent(ent.Owner, ref ev);

        if (ev.TemplateExecuted)
            ent.Comp.CombatMasteryCurrentCombo.Clear();
    }

    private static void ClearCombo(CombatMasteryComponent component)
    {
        component.CombatMasteryCurrentCombo.Clear();
        component.CurrentTarget = null;
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
            if (!comp.PendingMeleeDamageRefresh)
                continue;

            comp.PendingMeleeDamageRefresh = false;
            RefreshMeleeDamage((uid, comp));
        }
    }
}
