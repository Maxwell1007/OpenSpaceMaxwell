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

        HandleHelpInteraction(ent.Comp, ent.Owner, args.Target);
    }

    private void OnAttackAttempt(Entity<CombatMasteryComponent> ent, ref AttackAttemptEvent args)
    {
        if (args.Cancelled || args.Uid != ent.Owner || args.Target is not { } target)
            return;

        if (args.Disarm)
        {
            HandleDisarm(ent.Comp, target);
            return;
        }

        HandleAttack(ent.Comp, ent.Owner, target);
    }

    private void OnPullStarted(Entity<CombatMasteryComponent> ent, ref PullStartedMessage args)
    {
        if (args.PullerUid != ent.Owner)
            return;

        HandleGrab(ent.Comp, ent.Owner, args.PulledUid);
    }

    private void HandleHelpInteraction(CombatMasteryComponent component, EntityUid user, EntityUid target)
    {
        if (_hands.TryGetActiveItem(user, out _))
            return;

        UpdateCombo(component, target, ComboMasteryKeys.help);
    }

    private void HandleAttack(CombatMasteryComponent component, EntityUid user, EntityUid target)
    {
        if (_hands.TryGetActiveItem(user, out _))
        {
            ClearCombo(component);
            return;
        }

        UpdateCombo(component, target, ComboMasteryKeys.attack);
    }

    private void HandleGrab(CombatMasteryComponent component, EntityUid user, EntityUid target)
    {
        if (!_combatMode.IsInCombatMode(user) || !HasComp<MobStateComponent>(target))
            return;

        UpdateCombo(component, target, ComboMasteryKeys.grab);
    }

    private void HandleDisarm(CombatMasteryComponent component, EntityUid target)
    {
        if (!HasComp<MobStateComponent>(target))
            return;

        UpdateCombo(component, target, ComboMasteryKeys.disarm);
    }

    private static void UpdateCombo(CombatMasteryComponent component, EntityUid target, ComboMasteryKeys key)
    {
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
    }

    private static void ClearCombo(CombatMasteryComponent component)
    {
        component.CombatMasteryCurrentCombo.Clear();
        component.CurrentTarget = null;
    }
}
