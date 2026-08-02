using System;
using GamepadCompanion.Actions;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace GamepadCompanion.Gui;

// Dialog modal mínimo que captura el próximo evento de teclado del
// usuario y construye la acción con la combinación capturada
// (incluyendo modificadores Ctrl/Shift/Alt). Cancela con Escape o el
// botón "Cancelar"; en ambos casos onPicked recibe null.
//
// Modo hold (holdMode: true): la acción resultante es una HoldKeyAction —
// la tecla queda apretada mientras el botón del gamepad esté apretado.
//
// Modificadores solos: un Ctrl/Shift/Alt suelto NO se captura en el KeyDown,
// porque ahí todavía no sabemos si el usuario está armando "Ctrl+K" o quiere
// el modificador pelado. Se resuelve en el KeyUp: si soltó el modificador sin
// haber tocado ninguna otra tecla, era el modificador pelado. Sin esto no
// había forma de asignar Alt, que es lo que RKN Crafting usa de modificador
// por default (reportado por pngwn).
public sealed class KeyCaptureDialog : GuiDialog
{
    private const double DialogW = 360;
    private const double DialogH = 140;
    private const double TitleH  = 30;
    private const double Margin  = 16;
    private const double BodyH   = 40;
    private const double FooterH = 36;

    private readonly Action<IGameAction?> onPicked;
    private readonly bool holdMode;
    private bool captured;
    // Modificador que vimos bajar y todavía no resolvimos. -1 = ninguno.
    private int pendingModifier = -1;

    public override string ToggleKeyCombinationCode => null!;
    public override double DrawOrder => 0.7;
    public override bool PrefersUngrabbedMouse => true;
    public override bool CaptureAllInputs() => true;

    public KeyCaptureDialog(ICoreClientAPI capi,
                            Action<IGameAction?> onPicked,
                            bool holdMode = false) : base(capi)
    {
        this.onPicked = onPicked;
        this.holdMode = holdMode;
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
            .AddDialogTitleBar(Lang.Get(holdMode
                                   ? "gamepadcompanion:capture-hold-title"
                                   : "gamepadcompanion:capture-key-title"),
                               OnCancel, bounds: titleBarBounds)
            .BeginChildElements(bgBounds)
            .AddStaticText(Lang.Get(holdMode
                               ? "gamepadcompanion:capture-hold-prompt"
                               : "gamepadcompanion:capture-key-prompt"),
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

        // Un modificador solo no se resuelve todavía: puede ser el prefijo de
        // "Ctrl+K" o el binding en sí. Lo decide OnKeyUp.
        if (IsModifierKey(args.KeyCode))
        {
            args.Handled = true;
            pendingModifier = args.KeyCode;
            return;
        }

        pendingModifier = -1;
        Capture(args.KeyCode,
                args.CtrlPressed, args.ShiftPressed, args.AltPressed);
        args.Handled = true;
    }

    public override void OnKeyUp(KeyEvent args)
    {
        if (captured) return;
        if (args.KeyCode != pendingModifier) return;

        // Soltó el modificador sin haber tocado otra tecla → era el binding.
        // Los flags del propio KeyEvent no se re-envían: la tecla capturada YA
        // es el modificador, sumarlo como flag lo duplicaría.
        args.Handled = true;
        pendingModifier = -1;
        Capture(args.KeyCode, ctrl: false, shift: false, alt: false);
    }

    private void Capture(int keyCode, bool ctrl, bool shift, bool alt)
    {
        captured = true;
        // Un modificador pelado se captura SIEMPRE como hold, aunque el
        // usuario haya entrado por "[Asignar una tecla]". Un tap de Alt/Ctrl/
        // Shift no sirve para nada — nadie escucha su edge, todo el mundo los
        // lee como estado durante otra acción — y las dos entradas del picker
        // son fáciles de confundir: elegir la de tap daba exactamente el mismo
        // síntoma que el bug de RKN Crafting (modificador ausente en el click)
        // por una razón completamente distinta. El label resultante dice
        // "Mantener X", así que el usuario ve qué quedó asignado.
        bool holdIt = holdMode || IsModifierKey(keyCode);
        IGameAction action = holdIt
            ? new HoldKeyAction(keyCode, ctrl, shift, alt)
            : new KeyPressAction(keyCode, ctrl, shift, alt);
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
