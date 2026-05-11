using System;
using GamepadCompanion.Actions;
using GamepadCompanion.Input;
using Vintagestory.API.Client;

namespace GamepadCompanion.Gui;

// Rueda radial de 12 slots. Activación: mantener LB. Selección: stick derecho
// (con dead zone). Confirmación: soltar LB. Cancelación: B durante el hold.
//
// Estado interno:
//   - HighlightedSlot in [-1, 11]. -1 = stick en dead zone, sin selección activa.
//   - IsActive = IsOpened() del GuiDialog.
//
// Cuando el radial está abierto, GamepadInputDriver gatea los demás mappers
// (cámara, botones, triggers, toggles) para evitar que el R stick mueva la
// cámara o que B drop item al cancelar. Movement sigue habilitado a propósito
// — convencional para que se pueda caminar mientras se abre la rueda.
//
// Layout: 12 posiciones a 30° de separación, los labels van más afuera para
// dar espacio entre ellos (radius mayor + dialog más grande). SlotCount viene
// de SlotBindings.SlotCount para mantener sincronizado data ↔ render.
public sealed class RadialMenuDialog : HudElement
{
    private static int    SlotCount  => SlotBindings.SlotCount;
    private const double  Radius     = 170;
    private const double  DialogSize = 440;
    private const double  LabelW     = 96;
    private const double  LabelH     = 30;
    private const float   DeadZone   = 0.3f;

    private static readonly double[] ColorHighlighted = { 1.00, 0.84, 0.20, 1.00 };
    private static readonly double[] ColorIdle        = { 0.75, 0.75, 0.75, 0.85 };

    public int HighlightedSlot { get; private set; } = -1;
    public bool IsActive => IsOpened();

    // El ModSystem inyecta los bindings (cargados desde config) en
    // StartClientSide; este default es solo fallback defensivo si algo
    // falla antes de la inyección.
    public SlotBindings Bindings { get; set; } = SlotBindings.BuildDefault();

    public override double DrawOrder => 0.5;
    public override EnumDialogType DialogType => EnumDialogType.HUD;
    public override string ToggleKeyCombinationCode => null!;
    public override bool ShouldReceiveMouseEvents() => false;
    public override bool ShouldReceiveKeyboardEvents() => false;

    public RadialMenuDialog(ICoreClientAPI capi) : base(capi)
    {
        Compose();
    }

    public void OnGamepadTick(GamepadState current, GamepadState previous)
    {
        bool lbNow  = current.IsDown(GamepadButton.LeftBumper);
        bool lbPrev = previous.IsDown(GamepadButton.LeftBumper);
        bool isOpen = IsOpened();

        if (lbNow && !lbPrev && !isOpen)
        {
            HighlightedSlot = -1;
            Compose();
            TryOpen();
            return;
        }

        if (!isOpen) return;

        if (current.WasPressed(GamepadButton.B, previous))
        {
            HighlightedSlot = -1;
            TryClose();
            return;
        }

        int slot = ComputeSlot(current.RightStickX, current.RightStickY);
        if (slot != HighlightedSlot)
        {
            HighlightedSlot = slot;
            Compose();
        }

        if (!lbNow && lbPrev)
        {
            int confirmed = HighlightedSlot;
            var action = confirmed >= 0 ? Bindings[confirmed] : null;
            TryClose();
            // Diferimos al siguiente tick para que TryClose termine de procesar
            // antes de invocar la acción. Sin esto, hotkeys como `chat` que
            // dependen de pedir focus a un input field fallan: el engine ve la
            // rueda todavía "abierta" durante el frame en que invocamos al
            // handler y rechaza el focus request. Las hotkeys que solo abren
            // un GuiDialog (characterdialog, handbook) funcionan igual con o
            // sin el deferral.
            if (action is not null)
                capi.Event.RegisterCallback(_ => action.Execute(capi), 0);
        }
    }

    private static int ComputeSlot(float x, float y)
    {
        if (x * x + y * y < DeadZone * DeadZone) return -1;
        // Slot 0 a las 12 (arriba), clockwise. Provider invierte Y de forma que
        // arriba=+1, así que atan2(x, y) da 0 cuando arriba, pi/2 cuando derecha.
        float angle = MathF.Atan2(x, y);
        if (angle < 0) angle += MathF.Tau;
        return (int)MathF.Round(angle / (MathF.Tau / SlotCount)) % SlotCount;
    }

    private void Compose()
    {
        var dialogBounds = ElementBounds
            .Fixed(EnumDialogArea.CenterMiddle, 0, 0, DialogSize, DialogSize);
        // Fondo dialog-shape cuadrado semi-transparente para legibilidad
        // sobre escenas con mucho color. Cubre toda el área del radial con
        // un poco de padding visual respecto a los labels en el borde.
        var bgBounds = ElementBounds.Fixed(0, 0, DialogSize, DialogSize);

        var slotBounds = new ElementBounds[SlotCount];
        double cx = DialogSize / 2;
        double cy = DialogSize / 2;
        for (int i = 0; i < SlotCount; i++)
        {
            double angle = -Math.PI / 2 + i * (Math.PI * 2 / SlotCount);
            double sx = cx + Radius * Math.Cos(angle) - LabelW / 2;
            double sy = cy + Radius * Math.Sin(angle) - LabelH / 2;
            slotBounds[i] = ElementBounds.Fixed(sx, sy, LabelW, LabelH);
        }

        dialogBounds.WithChildren(slotBounds);

        var compo = capi.Gui
            .CreateCompo("gpcompanion-radial", dialogBounds)
            .AddShadedDialogBG(bgBounds, withTitleBar: false, alpha: 0.55f);
        for (int i = 0; i < SlotCount; i++)
        {
            var action = Bindings[i];
            string text = action?.Label ?? "—";
            // SmallishText (un escalón más grande que SmallText) + bold
            // mejora la legibilidad sobre el fondo del juego, especialmente
            // con el outline negro grueso que añadimos abajo.
            var font = CairoFont.WhiteSmallishText();
            font.Color = i == HighlightedSlot ? ColorHighlighted : ColorIdle;
            font.WithWeight(Cairo.FontWeight.Bold);
            font.WithStroke(new[] { 0.0, 0.0, 0.0, 0.9 }, 2.0);
            compo.AddStaticText(text, font,
                                EnumTextOrientation.Center, slotBounds[i]);
        }
        SingleComposer = compo.Compose();
    }
}
