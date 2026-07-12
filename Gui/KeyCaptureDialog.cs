using System;
using GamepadCompanion.Actions;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace GamepadCompanion.Gui;

// Dialog modal mínimo que captura el próximo evento de teclado del
// usuario y construye una KeyPressAction con la combinación capturada
// (incluyendo modificadores Ctrl/Shift/Alt). Cancela con Escape o el
// botón "Cancelar"; en ambos casos onPicked recibe null.
public sealed class KeyCaptureDialog : GuiDialog
{
    private const double DialogW = 360;
    private const double DialogH = 140;
    private const double TitleH  = 30;
    private const double Margin  = 16;
    private const double BodyH   = 40;
    private const double FooterH = 36;

    private readonly Action<KeyPressAction?> onPicked;
    private bool captured;

    public override string ToggleKeyCombinationCode => null!;
    public override double DrawOrder => 0.7;
    public override bool PrefersUngrabbedMouse => true;
    public override bool CaptureAllInputs() => true;

    public KeyCaptureDialog(ICoreClientAPI capi,
                            Action<KeyPressAction?> onPicked) : base(capi)
    {
        this.onPicked = onPicked;
        Compose();
    }

    private void Compose()
    {
        var dialogBounds = ElementStdBounds.AutosizedMainDialog
            .WithAlignment(EnumDialogArea.CenterMiddle);
        var bgBounds = ElementBounds.Fixed(0, 0, DialogW, DialogH);
        bgBounds.BothSizing = ElementSizing.FitToChildren;

        var titleBarBounds = ElementBounds.Fixed(0, 0, DialogW, TitleH);
        var bodyBounds = ElementBounds.Fixed(
            Margin, TitleH + Margin, DialogW - 2 * Margin, BodyH);
        var cancelBounds = ElementBounds.Fixed(
            Margin, DialogH - FooterH - Margin,
            DialogW - 2 * Margin, FooterH);

        SingleComposer = capi.Gui
            .CreateCompo("gpcompanion-keycap", dialogBounds)
            .AddShadedDialogBG(bgBounds)
            .AddDialogTitleBar(Lang.Get("gamepadcompanion:capture-key-title"),
                               OnCancel, bounds: titleBarBounds)
            .BeginChildElements(bgBounds)
            .AddStaticText(Lang.Get("gamepadcompanion:capture-key-prompt"),
                           CairoFont.WhiteSmallText(),
                           EnumTextOrientation.Center,
                           bodyBounds)
            .AddSmallButton(Lang.Get("gamepadcompanion:cancel"),
                            () => { OnCancel(); return true; },
                            cancelBounds)
            .EndChildElements()
            .Compose();
    }

    public override void OnKeyDown(KeyEvent args)
    {
        if (captured) return;

        // Escape se interpreta como cancel — convención estándar de VS.
        if (args.KeyCode == (int)GlKeys.Escape)
        {
            args.Handled = true;
            OnCancel();
            return;
        }

        // Ignorar pulsaciones de modificadores solos (Ctrl/Shift/Alt):
        // el user querrá una tecla "real" y los modificadores son
        // detectados via los flags del KeyEvent al apretar la real.
        if (IsModifierKey(args.KeyCode))
        {
            args.Handled = true;
            return;
        }

        captured = true;
        args.Handled = true;
        var action = new KeyPressAction(
            args.KeyCode,
            args.CtrlPressed,
            args.ShiftPressed,
            args.AltPressed);
        onPicked(action);
        TryClose();
    }

    private static bool IsModifierKey(int keyCode) =>
        keyCode == (int)GlKeys.ControlLeft  ||
        keyCode == (int)GlKeys.ControlRight ||
        keyCode == (int)GlKeys.ShiftLeft    ||
        keyCode == (int)GlKeys.ShiftRight   ||
        keyCode == (int)GlKeys.AltLeft      ||
        keyCode == (int)GlKeys.AltRight;

    private void OnCancel()
    {
        if (captured) return;
        captured = true;
        onPicked(null);
        TryClose();
    }
}
