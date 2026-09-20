using System;
using GamepadCompanion.Input;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace GamepadCompanion.Gui;

// La pregunta que se hace UNA vez, al que ya tenía el mod instalado cuando el
// layout de botones cambió: se queda con el de antes o pasa al nuevo.
//
// Existe porque un update que mueve la rueda, la navegación del hotbar y el
// modo precisión le rompe la memoria muscular a quien ya venía jugando, y eso
// se siente como que el mod se rompió — literalmente pasó, con el reporte de
// ModDB "the version i had last week was better (...) i have no clue how to get
// it back to where its useable for me". Preguntar cuesta un diálogo; adivinar
// mal cuesta un usuario.
//
// Es EnumDialogType.Dialog a propósito (el default de GuiDialog): así aparece
// el cursor virtual y se puede contestar con el mando, que es el único que el
// público de este mod tiene en la mano. Cerrarlo sin elegir cuenta como "dejá
// todo como estaba" — el callback sale igual con el fallback, así que la
// pregunta queda contestada y no vuelve a aparecer en cada sesión.
public sealed class LayoutPromptDialog : GuiDialog
{
    private const double DialogW = 470;
    // GuiStyle.TitleBarHeight es lo que el fondo reserva arriba (issue #7).
    private static readonly double TitleH = GuiStyle.TitleBarHeight;
    private const double Margin    = 16;
    private const double ParaGap   = 10;
    private const double ButtonsH  = 36;
    private const double ButtonGap = 10;

    private readonly GamepadLayoutKind fallback;
    private readonly Action<GamepadLayoutKind> onPick;
    private bool answered;

    public override string ToggleKeyCombinationCode => null!;
    public override double DrawOrder => 0.8;

    // fallback: lo que vale si cierra sin elegir. Es el layout que YA estaba
    // usando, nunca el nuevo: cerrar una ventana no puede cambiarle los botones.
    public LayoutPromptDialog(ICoreClientAPI capi, GamepadLayoutKind fallback,
                              Action<GamepadLayoutKind> onPick) : base(capi)
    {
        this.fallback = fallback;
        this.onPick = onPick;
        Compose();
    }

    // Las cuentas del alto, separadas y puras para poder correrlas sin el juego:
    // gpclab mide los cuatro textos con la tipografía REAL, en los tres idiomas
    // y a varios GUIScale, y le pregunta a esta misma función si el diálogo
    // entra en pantalla. Un port a mano de la fórmula se desincronizaría en
    // silencio, que es justo lo que no queremos (ver tools/gpclab/README.md).
    internal readonly record struct Geometry(
        double BodyW, double ButtonW,
        double IntroY, double ClassicY, double ModernY, double ButtonsY,
        double FootnoteY, double DialogH);

    internal static Geometry Measure(double introH, double classicH,
                                     double modernH, double footnoteH)
    {
        double bodyW = DialogW - 2 * Margin;
        double y = TitleH + Margin;
        double introY = y;
        y += introH + ParaGap;
        double classicY = y;
        y += classicH + ParaGap;
        double modernY = y;
        y += modernH + ParaGap + 4;
        double buttonsY = y;
        y += ButtonsH + ParaGap;
        double footnoteY = y;
        return new Geometry(bodyW, (bodyW - ButtonGap) / 2,
                            introY, classicY, modernY, buttonsY, footnoteY,
                            y + footnoteH + Margin);
    }

    private void Compose()
    {
        string intro    = Lang.Get("gamepadcompanion:layout-prompt-intro");
        string classic  = Lang.Get("gamepadcompanion:layout-classic-desc");
        string modern   = Lang.Get("gamepadcompanion:layout-modern-desc");
        string footnote = Lang.Get("gamepadcompanion:layout-prompt-later");

        double bodyW = DialogW - 2 * Margin;

        // Igual que KeyCaptureDialog: GuiElementStaticText envuelve al ancho
        // del bound pero dibuja igual aunque no entre a lo alto, así que un
        // alto fijo deja el texto largo (castellano, o un GUIScale alto)
        // metido abajo de los botones. Se mide y el diálogo crece.
        double introH    = TextHeight(intro,    bodyW, detail: false);
        double classicH  = TextHeight(classic,  bodyW, detail: true);
        double modernH   = TextHeight(modern,   bodyW, detail: true);
        double footnoteH = TextHeight(footnote, bodyW, detail: true);

        var g = Measure(introH, classicH, modernH, footnoteH);

        var introBounds    = ElementBounds.Fixed(Margin, g.IntroY,    bodyW, introH);
        var classicBounds  = ElementBounds.Fixed(Margin, g.ClassicY,  bodyW, classicH);
        var modernBounds   = ElementBounds.Fixed(Margin, g.ModernY,   bodyW, modernH);
        var footnoteBounds = ElementBounds.Fixed(Margin, g.FootnoteY, bodyW, footnoteH);

        double btnW = g.ButtonW;
        var classicBtn = ElementBounds.Fixed(Margin, g.ButtonsY, btnW, ButtonsH);
        var modernBtn  = ElementBounds.Fixed(Margin + btnW + ButtonGap, g.ButtonsY,
                                             btnW, ButtonsH);
        double dialogH = g.DialogH;

        // Sin FitToChildren: shrink-wrapear el fondo al contenido le come el
        // margen y lo deja más angosto que el title bar (issue #7).
        var bgBounds       = ElementBounds.Fixed(0, 0, DialogW, dialogH);
        var titleBarBounds = ElementBounds.Fixed(0, 0, DialogW, TitleH);

        SingleComposer = capi.Gui
            .CreateCompo("gpcompanion-layoutprompt",
                         ElementStdBounds.AutosizedMainDialog
                             .WithAlignment(EnumDialogArea.CenterMiddle))
            .AddShadedDialogBG(bgBounds)
            .AddDialogTitleBar(Lang.Get("gamepadcompanion:layout-prompt-title"),
                               () => Pick(fallback), bounds: titleBarBounds)
            .BeginChildElements(bgBounds)
            .AddStaticText(intro,   CairoFont.WhiteSmallText(),  introBounds)
            .AddStaticText(classic, CairoFont.WhiteDetailText(), classicBounds)
            .AddStaticText(modern,  CairoFont.WhiteDetailText(), modernBounds)
            // Los labels se recortan igual que en el resto del mod: un botón
            // de VS crece solo hasta que el texto entre en una línea, y nadie
            // lo clippea (ver GuiTextFit).
            .AddSmallButton(GuiTextFit.EllipsizeButton(
                                GamepadLayout.NameOf(GamepadLayoutKind.Classic), btnW),
                            () => { Pick(GamepadLayoutKind.Classic); return true; },
                            classicBtn, EnumButtonStyle.Normal)
            .AddSmallButton(GuiTextFit.EllipsizeButton(
                                GamepadLayout.NameOf(GamepadLayoutKind.Modern), btnW),
                            () => { Pick(GamepadLayoutKind.Modern); return true; },
                            modernBtn, EnumButtonStyle.Normal)
            .AddStaticText(footnote, CairoFont.WhiteDetailText(), footnoteBounds)
            .EndChildElements()
            .Compose();
    }

    private double TextHeight(string text, double width, bool detail)
    {
        double scale = RuntimeEnv.GUIScale;
        if (scale <= 0) scale = 1;
        CairoFont font = detail ? CairoFont.WhiteDetailText() : CairoFont.WhiteSmallText();
        return capi.Gui.Text.GetMultilineTextHeight(
                   font, text, GuiElement.scaled(width)) / scale;
    }

    private void Pick(GamepadLayoutKind kind)
    {
        // Una sola vez: el click cierra el diálogo, y OnGuiClosed volvería a
        // entrar acá con el fallback y pisaría la elección del usuario.
        if (answered) return;
        answered = true;
        onPick(kind);
        TryClose();
    }

    // Escape, el aspa del title bar, o cualquier otro camino de cierre.
    public override void OnGuiClosed()
    {
        base.OnGuiClosed();
        Pick(fallback);
    }
}
