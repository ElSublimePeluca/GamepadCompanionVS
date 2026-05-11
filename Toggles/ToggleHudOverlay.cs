using Vintagestory.API.Client;

namespace GamepadCompanion.Toggles;

// HUD overlay con indicadores ("CTRL", "SHIFT", "PRECISIÓN") en la esquina
// superior derecha. Color dorado cuando el toggle correspondiente está
// activo, gris atenuado cuando inactivo, paréntesis cuando suspendido
// (GUI abierta o focus perdido). Se reconstruye solo cuando cambia el
// estado, no cada frame.
public sealed class ToggleHudOverlay : HudElement
{
    private static readonly double[] ColorActive    = { 1.0, 0.84, 0.20, 1.0 };
    private static readonly double[] ColorInactive  = { 0.55, 0.55, 0.55, 0.85 };
    private static readonly double[] ColorSuspended = { 0.55, 0.55, 0.55, 0.55 };

    private readonly ToggleManager toggles;

    private bool lastCtrl;
    private bool lastShift;
    private bool lastPrecision;
    private bool lastSuspended;

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
        Compose();
        TryOpen();
    }

    private void Compose()
    {
        bool suspended = toggles.Suspended;

        // Bounds locales al dialog (no al screen), para que el dialog sea
        // chiquito y solo cubra el área de los labels. Evita que el overlay
        // se reporte ocupando toda la pantalla aunque visualmente sea minúsculo.
        // PRECISIÓN necesita un poco más de ancho que CTRL/SHIFT.
        var ctrlBounds      = ElementBounds.Fixed(0,  0,  110, 24);
        var shiftBounds     = ElementBounds.Fixed(0, 26,  110, 24);
        var precisionBounds = ElementBounds.Fixed(0, 52,  110, 24);

        var dialogBounds = ElementBounds
            .Fixed(EnumDialogArea.RightTop, -16, 12, 110, 78)
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
            .AddStaticText(LabelFor("PRECISIÓN", toggles.PrecisionActive,
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

        if (ctrl != lastCtrl || shift != lastShift || precision != lastPrecision
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
