using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Shared._OpenSpace.Combat.CombatMastery;
using Content.Shared._OpenSpace.Combat.CombatMastery.Hud.Components;
using Robust.Client.Graphics;
using Robust.Client.Input;
using Robust.Client.ResourceManagement;
using Robust.Client.UserInterface;
using Robust.Shared.Enums;
using Robust.Shared.Graphics.RSI;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Client._OpenSpace.Combat.CombatMastery.Hud;

public sealed class CombatMasteryComboHudOverlay : Overlay
{
    private const float CursorOffset = 10f;
    private const float IconSpacing = 10f;

    [Dependency] private readonly IInputManager _inputManager = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IResourceCache _resourceCache = default!;

    private readonly CombatMasteryComboHudSystem _hudSystem;
    private readonly Dictionary<ComboMasteryKeys, AnimatedComboIcon> _icons = [];

    public override OverlaySpace Space => OverlaySpace.ScreenSpace;

    public CombatMasteryComboHudOverlay(CombatMasteryComboHudSystem hudSystem)
    {
        IoCManager.InjectDependencies(this);

        _hudSystem = hudSystem;

        LoadIcon(ComboMasteryKeys.help);
        LoadIcon(ComboMasteryKeys.attack);
        LoadIcon(ComboMasteryKeys.disarm);
        LoadIcon(ComboMasteryKeys.grab);
    }

    protected override bool BeforeDraw(in OverlayDrawArgs args)
    {
        return _hudSystem.TryGetVisibleCombo(out _);
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        if (!_hudSystem.TryGetVisibleCombo(out var combo) || combo == null)
            return;

        var visibleIcons = combo
            .TakeLast(CombatMasteryComboHudComponent.MaxVisibleKeys)
            .Where(_icons.ContainsKey)
            .ToArray();

        if (visibleIcons.Length == 0)
            return;

        var frames = visibleIcons
            .Select(GetCurrentFrame)
            .Where(texture => texture != null)
            .Cast<Texture>()
            .ToArray();

        if (frames.Length == 0)
            return;

        var maxWidth = frames.Max(texture => texture.Width);
        var mousePos = _inputManager.MouseScreenPosition.Position;
        var drawPos = new Vector2(
            mousePos.X - CursorOffset - maxWidth,
            mousePos.Y);

        foreach (var frame in frames)
        {
            args.ScreenHandle.DrawTextureRect(frame, UIBox2.FromDimensions(drawPos, new Vector2(frame.Width, frame.Height)));
            drawPos.Y += frame.Height + IconSpacing;
        }
    }

    private void LoadIcon(ComboMasteryKeys key)
    {
        var stateName = key.ToString();
        var rsiPath = new ResPath($"/Textures/_OpenSpace/Interface/Combat/CombatMastery/HudCombo/{stateName}.rsi");

        if (!_resourceCache.TryGetResource<RSIResource>(rsiPath, out var rsiResource) ||
            !rsiResource.RSI.TryGetState(stateName, out RSI.State? state))
        {
            return;
        }

        _icons[key] = new AnimatedComboIcon(state.GetFrames(RsiDirection.South), state.GetDelays());
    }

    private Texture? GetCurrentFrame(ComboMasteryKeys key)
    {
        if (!_icons.TryGetValue(key, out var icon) || icon.Frames.Length == 0)
            return null;

        if (icon.Delays.Length == 0 || icon.Frames.Length == 1)
            return icon.Frames[0];

        var totalDuration = 0f;
        foreach (var delay in icon.Delays)
        {
            totalDuration += delay;
        }

        if (totalDuration <= 0f)
            return icon.Frames[0];

        var animationTime = (float) (_timing.CurTime.TotalSeconds % totalDuration);
        var accumulatedDelay = 0f;

        for (var index = 0; index < icon.Delays.Length; index++)
        {
            accumulatedDelay += icon.Delays[index];
            if (animationTime < accumulatedDelay)
                return icon.Frames[index];
        }

        return icon.Frames[^1];
    }

    private sealed record AnimatedComboIcon(Texture[] Frames, float[] Delays);
}
