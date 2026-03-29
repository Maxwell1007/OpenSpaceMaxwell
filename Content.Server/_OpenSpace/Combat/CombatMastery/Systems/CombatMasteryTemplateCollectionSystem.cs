using Content.Server._OpenSpace.Combat.CombatMastery.Components;
using Content.Server._OpenSpace.Combat.CombatMastery.Hud;

namespace Content.Server._OpenSpace.Combat.CombatMastery.Systems;

public abstract class CombatMasteryTemplateCollectionSystem<TComponent> : EntitySystem
    where TComponent : Component, ICombatMasteryTemplateProvider
{
    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<TComponent, CombatMasteryComboUpdatedEvent>(HandleComboUpdated);
        SubscribeLocalEvent<TComponent, CombatMasteryActiveStateQueryEvent>(OnActiveStateQuery);
        SubscribeLocalEvent<TComponent, ComponentStartup>(OnMasteryStartup);
        SubscribeLocalEvent<TComponent, ComponentShutdown>(OnMasteryShutdown);
    }

    public bool TryAddOrReplaceTemplate(Entity<TComponent> ent, CombatMasteryTemplate template)
    {
        if (!ent.Comp.TemplateCollection.TryAddOrReplaceTemplate(template))
            return false;

        Dirty(ent);
        return true;
    }

    public bool TryRemoveTemplate(Entity<TComponent> ent, string templateName)
    {
        if (!ent.Comp.TemplateCollection.TryRemoveTemplate(templateName))
            return false;

        Dirty(ent);
        return true;
    }

    private void HandleComboUpdated(Entity<TComponent> ent, ref CombatMasteryComboUpdatedEvent args)
    {
        OnComboUpdated(ent, ref args);

        if (args.TemplateExecuted)
            return;

        if (!ent.Comp.TemplateCollection.TryFindTailMatch(args.Combo, out var matchedTemplate))
            return;

        if (matchedTemplate == null)
            return;

        args.TemplateExecuted = OnTemplateMatched(ent, args.Target, matchedTemplate);
    }

    private static void OnActiveStateQuery(Entity<TComponent> ent, ref CombatMasteryActiveStateQueryEvent args)
    {
        args.HasActiveMastery = true;
    }

    private void OnMasteryStartup(Entity<TComponent> ent, ref ComponentStartup args)
    {
        RefreshHudState(ent.Owner);
        OnMasteryStarted(ent, ref args);
    }

    private void OnMasteryShutdown(Entity<TComponent> ent, ref ComponentShutdown args)
    {
        RefreshHudState(ent.Owner);
        OnMasteryStopped(ent, ref args);
    }

    protected virtual void OnComboUpdated(Entity<TComponent> ent, ref CombatMasteryComboUpdatedEvent args)
    {
        if (args.TemplateExecuted)
            return;
    }

    protected virtual void OnMasteryStarted(Entity<TComponent> ent, ref ComponentStartup args)
    {
    }

    protected virtual void OnMasteryStopped(Entity<TComponent> ent, ref ComponentShutdown args)
    {
    }

    protected abstract bool OnTemplateMatched(Entity<TComponent> ent, EntityUid target, CombatMasteryTemplate template);

    private void RefreshHudState(EntityUid uid)
    {
        var ev = new CombatMasteryHudRefreshEvent();
        RaiseLocalEvent(uid, ref ev);
    }
}
