using Content.Server._OpenSpace.Combat.Components;

namespace Content.Server._OpenSpace.Combat.Systems;

public abstract class CombatMasteryTemplateCollectionSystem<TComponent> : EntitySystem
    where TComponent : Component, ICombatMasteryTemplateProvider
{
    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<TComponent, CombatMasteryComboUpdatedEvent>(OnComboUpdated);
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

    private void OnComboUpdated(Entity<TComponent> ent, ref CombatMasteryComboUpdatedEvent args)
    {
        if (args.TemplateExecuted)
            return;

        if (!ent.Comp.TemplateCollection.TryFindTailMatch(args.Combo, out var matchedTemplate))
            return;

        if (matchedTemplate == null)
            return;

        OnTemplateMatched(ent, args.Target, matchedTemplate);
        args.TemplateExecuted = true;
    }

    protected abstract void OnTemplateMatched(Entity<TComponent> ent, EntityUid target, CombatMasteryTemplate template);
}
