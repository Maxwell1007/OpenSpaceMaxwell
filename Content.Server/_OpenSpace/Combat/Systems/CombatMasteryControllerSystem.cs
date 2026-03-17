using Content.Server._OpenSpace.Combat.Components;
using Content.Shared.CombatMode;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Interaction.Events;
using Content.Shared.Mobs.Components;
using Content.Shared.Movement.Pulling.Events;

namespace Content.Server._OpenSpace.Combat.Systems;

public sealed class CombatMasteryControllerSystem : EntitySystem
{
    [Dependency] private readonly SharedCombatModeSystem _combatMode = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<CombatMasteryComponent, UserInteractHandEvent>(OnUserInteractHand);
        SubscribeLocalEvent<CombatMasteryComponent, AttackAttemptEvent>(OnAttackAttempt);
        SubscribeLocalEvent<CombatMasteryComponent, PullStartedMessage>(OnPullStarted);
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
        if (_hands.TryGetActiveItem(ent.Owner, out _))
        {
            ClearCombo(ent.Comp);
            return;
        }

        UpdateCombo(ent, target, ComboMasteryKeys.attack);
    }

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
}
