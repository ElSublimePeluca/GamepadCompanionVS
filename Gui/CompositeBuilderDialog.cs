using System;
using System.Collections.Generic;
using GamepadCompanion.Actions;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace GamepadCompanion.Gui;

// Constructor de CompositeAction. Hasta 4 pasos fijos; cada uno abre el
// HotKeyPickerDialog estándar y se rellena al elegir. "Aceptar" arma una
// CompositeAction con los pasos no vacíos en orden y la entrega vía
// onConfirm. "Cancelar" no toca nada.
//
// Sin UI para añadir/quitar pasos: 4 fijos cubren los casos comunes y
// evitan complejidad de recompose. Pasos vacíos al final se ignoran.
public sealed class CompositeBuilderDialog : GuiDialog
{
    public  const int    MaxSteps    = 4;
    private const double DialogW     = 460;
    // GuiStyle.TitleBarHeight es lo que el fondo reserva arriba (issue #7).
    private static readonly double TitleH = GuiStyle.TitleBarHeight;
    private const double Margin      = 16;
    private const double RowH        = 32;
    private const double RowGap      = 6;
    private const double LabelW      = 60;
    private const double FooterH     = 36;
    private const double FooterGap   = 12;

    private readonly IReadOnlyList<string> entryNames;
    private readonly Func<int, IGameAction?> resolveAction;
    private readonly Func<IGameAction, int> indexOfAction;
    private readonly Action<CompositeAction?> onConfirm;

    private readonly IGameAction?[] steps = new IGameAction?[MaxSteps];

    public override string ToggleKeyCombinationCode => null!;
    public override double DrawOrder => 0.65;

    public CompositeBuilderDialog(
        ICoreClientAPI capi,
        IReadOnlyList<string> entryNames,
        Func<int, IGameAction?> resolveAction,
        Func<IGameAction, int> indexOfAction,
        CompositeAction? initial,
        Action<CompositeAction?> onConfirm) : base(capi)
    {
        this.entryNames = entryNames;
        this.resolveAction = resolveAction;
        this.indexOfAction = indexOfAction;
        this.onConfirm = onConfirm;

        if (initial is not null)
        {
            for (int i = 0; i < Math.Min(initial.Children.Count, MaxSteps); i++)
                steps[i] = initial.Children[i];
        }

        Compose();
    }

    private void Compose()
    {
        var dialogBounds = ElementStdBounds.AutosizedMainDialog
            .WithAlignment(EnumDialogArea.CenterMiddle);

        double bodyY = TitleH + Margin;
        double totalH = bodyY + MaxSteps * (RowH + RowGap) + Margin
                              + FooterH + Margin;
        // Nada de FitToChildren acá: shrink-wrapear el fondo al contenido
        // le come el margen derecho/inferior y lo deja más angosto que el
        // title bar. Ver ConfigDialog.Compose (issue #7).
        var bgBounds = ElementBounds.Fixed(0, 0, DialogW, totalH);

        var titleBarBounds = ElementBounds.Fixed(0, 0, DialogW, TitleH);

        var compo = capi.Gui
            .CreateCompo("gpcompanion-composite", dialogBounds)
            .AddShadedDialogBG(bgBounds)
            .AddDialogTitleBar(Lang.Get("gamepadcompanion:composite-title"),
                               OnCancel, bounds: titleBarBounds)
            .BeginChildElements(bgBounds);

        double y = bodyY;
        for (int i = 0; i < MaxSteps; i++)
        {
            int step = i;
            var labelBounds = ElementBounds
                .Fixed(Margin, y + 4, LabelW, RowH);
            var btnBounds = ElementBounds
                .Fixed(Margin + LabelW + 8, y,
                       DialogW - 2 * Margin - LabelW - 8, RowH);

            string stepLabel = GuiTextFit.EllipsizeButton(
                steps[i]?.Label ?? Lang.Get("gamepadcompanion:entry-empty"),
                DialogW - 2 * Margin - LabelW - 8);

            compo.AddStaticText(Lang.Get("gamepadcompanion:step-label", i + 1),
                                CairoFont.WhiteSmallText(), labelBounds);
            compo.AddSmallButton(stepLabel,
                                 () => { OpenPicker(step); return true; },
                                 btnBounds, EnumButtonStyle.Normal);
            y += RowH + RowGap;
        }

        double btnW = (DialogW - 2 * Margin - FooterGap) / 2;
        double footerY = y + Margin;
        var okBounds = ElementBounds.Fixed(Margin, footerY, btnW, FooterH);
        var cancelBounds = ElementBounds.Fixed(
            Margin + btnW + FooterGap, footerY, btnW, FooterH);

        compo.AddSmallButton(Lang.Get("gamepadcompanion:ok"),
                             OnConfirm, okBounds);
        compo.AddSmallButton(Lang.Get("gamepadcompanion:cancel"),
                             () => { OnCancel(); return true; },
                             cancelBounds);

        compo.EndChildElements();
        SingleComposer = compo.Compose();
    }

    private void OpenPicker(int step)
    {
        int currentIdx = steps[step] is null ? 0 : indexOfAction(steps[step]!);
        var picker = new HotKeyPickerDialog(
            capi,
            Lang.Get("gamepadcompanion:pick-action-title",
                     Lang.Get("gamepadcompanion:step-label", step + 1)),
            entryNames,
            currentIdx,
            picked =>
            {
                steps[step] = picked <= 0 ? null : resolveAction(picked);
                Compose();
            });
        picker.TryOpen();
    }

    private bool OnConfirm()
    {
        var children = new List<IGameAction>();
        foreach (var s in steps)
            if (s is not null) children.Add(s);

        var result = children.Count == 0 ? null : new CompositeAction(children);
        onConfirm(result);
        TryClose();
        return true;
    }

    private void OnCancel() => TryClose();
}
