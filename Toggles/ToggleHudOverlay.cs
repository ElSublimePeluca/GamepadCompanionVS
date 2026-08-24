using System;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace GamepadCompanion.Toggles;

// HUD overlay con indicadores ("CTRL", "SHIFT", "PRECISIÓN") en la esquina
// superior derecha. Color dorado cuando el toggle correspondiente está
// activo, gris atenuado cuando inactivo, paréntesis cuando suspendido
// (GUI abierta o focus perdido). Se reconstruye solo cuando cambia el
// estado, no cada frame.
//
// Si el minimap vanilla está pinneado en esa misma esquina, el overlay se
// corre justo debajo suyo (issue #6: se encimaban). Ver MinimapAnchor.
public sealed class ToggleHudOverlay : HudElement
{
    private static readonly double[] ColorActive    = { 1.0, 0.84, 0.20, 1.0 };
    private static readonly double[] ColorInactive  = { 0.55, 0.55, 0.55, 0.85 };
    private static readonly double[] ColorSuspended = { 0.55, 0.55, 0.55, 0.55 };

    private const double OverlayW  = 110;
    private const double RowH      = 24;
    private const double RowGap    = 2;
    private const double OverlayH  = 3 * RowH + 2 * RowGap;

    // Posición cuando la esquina está libre (la de toda la vida).
    private const double FreeX = -16;
    private const double FreeY = 12;
    // Aire entre el borde inferior del minimap y la primera línea.
    private const double MinimapGap = 6;

    // Cada cuánto releemos la geometría del minimap. Es una pasada por
    // OpenedGuis: barata, pero no hace falta hacerla 60 veces por segundo —
    // el minimap solo se mueve cuando el usuario toca settings o F6.
    private const float ProbeInterval = 0.25f;

    private readonly ToggleManager toggles;
    private readonly MinimapAnchor minimap;

    private bool lastCtrl;
    private bool lastShift;
    private bool lastPrecision;
    private bool lastSuspended;

    private double anchorX = FreeX;
    private double anchorY = FreeY;
    // Arranca vencido: en el constructor el minimap todavía no compuso sus
    // bounds, así que queremos medir en el primer frame y no 250ms después.
    private float probeAccum = ProbeInterval;

    public override double DrawOrder => 0.2;
    public override EnumDialogType DialogType => EnumDialogType.HUD;
    public override string ToggleKeyCombinationCode => null!;

    // Crítico: el HUD nunca debe consumir eventos del mouse. Si los recibe, el
    // engine los marca como "handled" y los clicks del mouse físico no llegan
    // al gameplay (donde se procesan via OnMouseDownRaw → InWorldMouseState).
    // Combinado con el bound chiquito de Compose, esto asegura que el overlay
    // sea visualmente presente pero transparente al input.
    public override bool ShouldReceiveMouseEvents() => false;
    public override bool ShouldReceiveKeyboardEvents() => false;

    public ToggleHudOverlay(ICoreClientAPI capi, ToggleManager toggles) : base(capi)
    {
        this.toggles = toggles;
        minimap = new MinimapAnchor(capi);
        RefreshAnchor();
        Compose();
        TryOpen();
    }

    // Devuelve true si el ancla se movió lo suficiente como para justificar
    // un recompose.
    private bool RefreshAnchor()
    {
        double? minimapBottom = minimap.RightTopBottomEdge();

        // Debajo del minimap alineamos los bordes derechos usando el mismo
        // padding a pantalla que usa vanilla para pinnearlo, así las dos
        // cajas quedan a plomo.
        double x = minimapBottom is null ? FreeX : -GuiStyle.DialogToScreenPadding;
        double y = minimapBottom is null ? FreeY : minimapBottom.Value + MinimapGap;

        if (Math.Abs(x - anchorX) < 0.5 && Math.Abs(y - anchorY) < 0.5) return false;

        anchorX = x;
        anchorY = y;
        return true;
    }

    private void Compose()
    {
        bool suspended = toggles.Suspended;

        // Bounds locales al dialog (no al screen), para que el dialog sea
        // chiquito y solo cubra el área de los labels. Evita que el overlay
        // se reporte ocupando toda la pantalla aunque visualmente sea minúsculo.
        // PRECISIÓN necesita un poco más de ancho que CTRL/SHIFT.
        var ctrlBounds      = ElementBounds.Fixed(0, 0 * (RowH + RowGap), OverlayW, RowH);
        var shiftBounds     = ElementBounds.Fixed(0, 1 * (RowH + RowGap), OverlayW, RowH);
        var precisionBounds = ElementBounds.Fixed(0, 2 * (RowH + RowGap), OverlayW, RowH);

        var dialogBounds = ElementBounds
            .Fixed(EnumDialogArea.RightTop, anchorX, anchorY, OverlayW, OverlayH)
            .WithChildren(ctrlBounds, shiftBounds, precisionBounds);

        SingleComposer = capi.Gui
            .CreateCompo("gpcompanion-toggles", dialogBounds)
            .AddStaticText(LabelFor("CTRL", toggles.CtrlActive, suspended),
                           FontFor(toggles.CtrlActive, suspended),
                           EnumTextOrientation.Right,
                           ctrlBounds)
            .AddStaticText(LabelFor("SHIFT", toggles.ShiftActive, suspended),
                           FontFor(toggles.ShiftActive, suspended),
                           EnumTextOrientation.Right,
                           shiftBounds)
            .AddStaticText(LabelFor(Lang.Get("gamepadcompanion:hud-precision"),
                                    toggles.PrecisionActive,
                                    suspended),
                           FontFor(toggles.PrecisionActive, suspended),
                           EnumTextOrientation.Right,
                           precisionBounds)
            .Compose();
    }

    private static string LabelFor(string name, bool active, bool suspended)
    {
        if (suspended && active) return $"({name})";
        return name;
    }

    private static CairoFont FontFor(bool active, bool suspended)
    {
        var font = CairoFont.WhiteSmallText();
        font.Color = !active ? ColorInactive
                   : suspended ? ColorSuspended
                   : ColorActive;
        font.WithStroke(new[] { 0.0, 0.0, 0.0, 0.7 }, 1.5);
        return font;
    }

    public override void OnRenderGUI(float deltaTime)
    {
        bool ctrl = toggles.CtrlActive;
        bool shift = toggles.ShiftActive;
        bool precision = toggles.PrecisionActive;
        bool sus = toggles.Suspended;

        bool anchorMoved = false;
        probeAccum += deltaTime;
        if (probeAccum >= ProbeInterval)
        {
            probeAccum = 0;
            anchorMoved = RefreshAnchor();
        }

        if (anchorMoved
            || ctrl != lastCtrl || shift != lastShift || precision != lastPrecision
            || sus != lastSuspended)
        {
            lastCtrl = ctrl;
            lastShift = shift;
            lastPrecision = precision;
            lastSuspended = sus;
            Compose();
        }

        base.OnRenderGUI(deltaTime);
    }
}
