using System;
using System.Collections.Generic;
using Content.Server._OpenSpace.Combat.Systems;
using Content.Shared.Damage;

namespace Content.Server._OpenSpace.Combat.Components;

[RegisterComponent, ComponentProtoName("CombatMastery")]
[Access(typeof(CombatMasteryControllerSystem))]
public sealed partial class CombatMasteryComponent : Component
{
    [DataField]
    public int MaxComboLength = 12;

    [DataField]
    public List<ComboMasteryKeys> CombatMasteryCurrentCombo = new();

    [DataField]
    public EntityUid? CurrentTarget;

    [DataField]
    public CombatMasteryTemplateCollection TemplateCollection = new();

    [ViewVariables]
    public DamageSpecifier? OriginalUnarmedMeleeDamage;

    [ViewVariables]
    public bool PendingMeleeDamageRefresh;
}

[DataDefinition]
public sealed partial class CombatMasteryTemplateCollection
{
    [DataField]
    private List<CombatMasteryTemplate> _templates = [];

    [NonSerialized]
    private readonly Dictionary<ComboMasteryKeys, List<CombatMasteryTemplate>> _templatesByTailKey = [];

    [NonSerialized]
    private bool _lookupDirty = true;

    public bool TryFindTailMatch(IReadOnlyList<ComboMasteryKeys> combo, out CombatMasteryTemplate? matchedTemplate)
    {
        matchedTemplate = null;

        if (combo.Count == 0)
            return false;

        RebuildLookupIfNeeded();

        var tailKey = combo[^1];
        if (!_templatesByTailKey.TryGetValue(tailKey, out var candidates))
            return false;

        foreach (var candidate in candidates)
        {
            if (!TailMatches(combo, candidate.Sequence))
                continue;

            matchedTemplate = candidate;
            return true;
        }

        return false;
    }

    public bool TryAddOrReplaceTemplate(CombatMasteryTemplate template)
    {
        if (!template.IsValid())
            return false;

        var existingIndex = _templates.FindIndex(x => x.Name == template.Name);
        if (existingIndex >= 0)
            _templates[existingIndex] = template;
        else
            _templates.Add(template);

        _lookupDirty = true;
        return true;
    }

    public bool TryRemoveTemplate(string templateName)
    {
        if (string.IsNullOrWhiteSpace(templateName))
            return false;

        var removed = _templates.RemoveAll(x => x.Name == templateName) > 0;
        if (removed)
            _lookupDirty = true;

        return removed;
    }

    private void RebuildLookupIfNeeded()
    {
        if (!_lookupDirty)
            return;

        _templatesByTailKey.Clear();

        foreach (var template in _templates)
        {
            if (!template.IsValid())
                continue;

            var tailKey = template.Sequence[^1];
            if (!_templatesByTailKey.TryGetValue(tailKey, out var bucket))
            {
                bucket = [];
                _templatesByTailKey[tailKey] = bucket;
            }

            bucket.Add(template);
        }

        foreach (var bucket in _templatesByTailKey.Values)
        {
            bucket.Sort(static (left, right) => left.Sequence.Count.CompareTo(right.Sequence.Count));
        }

        _lookupDirty = false;
    }

    private static bool TailMatches(IReadOnlyList<ComboMasteryKeys> combo, IReadOnlyList<ComboMasteryKeys> template)
    {
        if (template.Count > combo.Count)
            return false;

        var offset = combo.Count - template.Count;
        for (var index = 0; index < template.Count; index++)
        {
            if (combo[offset + index] != template[index])
                return false;
        }

        return true;
    }
}
