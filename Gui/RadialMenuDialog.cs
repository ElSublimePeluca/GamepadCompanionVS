using System;
using GamepadCompanion.Actions;
using GamepadCompanion.Input;
using Vintagestory.API.Client;

namespace GamepadCompanion.Gui;

// Cómo puede abrirse la rueda en este tick. Lo decide el driver con
// RadialMenuDialog.OpeningFor, según el botón y lo que haya abierto.
public enum WheelOpening
{
    // Al apretar el botón, que es lo de siempre: en el mundo, y en cualquier
    // contexto cuando el botón no es del D-pad (LB en el clásico).
    OnPress,
    // Al MANTENERLO RadialMenuDialog.HoldToOpen segundos. Con un menú abierto
    // y la rueda en el D-pad: el toque es del cursor, que salta al slot de
    // arriba, y el hold es de la rueda.
    OnHold,
    // Nunca. Con el teclado virtual abierto y la rueda en el D-pad, el D-pad
    // entero es del teclado.
    Never,
}

// Rueda radial de 12 slots. Activación: mantener el botón que diga el layout
// (LB en el clásico, D-pad ↑ en el nuevo; con un menú abierto el D-pad pide un
// hold de verdad, ver OpeningFor). Selección: stick derecho (con dead zone).
// Confirmación: soltar ese botón. Cancelación: B durante el hold.
//
// Estado interno:
//   - HighlightedSlot in [-1, 11]. -1 = stick en dead zone, sin selección activa.
//   - IsActive = IsOpened() del GuiDialog.
//
// Cuando el radial está abierto, GamepadInputDriver gatea los demás mappers
// (cámara, botones, triggers, toggles) para evitar que el R stick mueva la
// cámara o que B drop item al cancelar — también en el tick en que se cierra,
// que es justo el de B (ver wheelBusy en el driver). Movement sigue habilitado
// a propósito — convencional para que se pueda caminar mientras se abre la
// rueda.
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

    // Cuánto hay que mantener el botón para abrir la rueda cuando el toque es
    // de otro (WheelOpening.OnHold). El mismo número que el retardo de
    // repetición del hotbar (ButtonMapper.RepeatDelay), por la misma razón:
    // más largo que cualquier toque, y lo bastante corto para no sentirse
    // como una espera.
    internal const float HoldToOpen = 0.40f;

    // Cuánto lleva apretado el botón desde un flanco que todavía no abrió la
    // rueda. null = no hay ninguna espera en curso.
    private float? waited;

    private static readonly double[] ColorHighlighted = { 1.00, 0.84, 0.20, 1.00 };
    private static readonly double[] ColorIdle        = { 0.75, 0.75, 0.75, 0.85 };

    public int HighlightedSlot { get; private set; } = -1;
    public bool IsActive => IsOpened();

    // El ModSystem inyecta los bindings (cargados desde config) en
    // StartClientSide; este default es solo fallback defensivo si algo
    // falla antes de la inyección.
    public SlotBindings Bindings { get; set; } = SlotBindings.BuildDefault();

    // Botón que abre la rueda mientras se lo mantiene. Lo fija el ModSystem
    // desde el layout activo; el default es el de siempre por si algo falla
    // antes de esa inyección. ButtonMapper no le ejecuta ni binding ni
    // default: el hold ya significa esto.
    public GamepadButton OpenButton { get; set; } = GamepadButton.LeftBumper;

    public override double DrawOrder => 0.5;
    public override EnumDialogType DialogType => EnumDialogType.HUD;
    public override string ToggleKeyCombinationCode => null!;
    public override bool ShouldReceiveMouseEvents() => false;
    public override bool ShouldReceiveKeyboardEvents() => false;

    public RadialMenuDialog(ICoreClientAPI capi) : base(capi)
    {
        Compose();
    }

    // `opening` gobierna SÓLO la apertura, no la rueda ya abierta: con la
    // rueda abierta el driver ya cortó todo lo demás, así que ahí no hay
    // conflicto que resolver, sea cual sea el botón.
    public void OnGamepadTick(GamepadState current, GamepadState previous,
                              float dt, WheelOpening opening)
    {
        bool holdNow  = current.IsDown(OpenButton);
        bool holdPrev = previous.IsDown(OpenButton);
        bool isOpen = IsOpened();

        if (!isOpen)
        {
            (waited, bool open) = StepOpening(waited, holdNow, holdPrev, opening, dt);
            if (!open) return;
            HighlightedSlot = -1;
            Compose();
            TryOpen();
            return;
        }

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

        if (!holdNow && holdPrev)
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

    // Cómo se abre la rueda según dónde esté su botón y qué haya abierto.
    //
    // Con el botón en el D-pad (layout nuevo: ↑), un menú se lo queda para
    // mover el cursor, y hasta 1.14.0 la rueda directamente no se abría ahí.
    // Eso dejaba afuera más de lo que parecía: el chat enfocado cuenta como
    // diálogo modal (HudDialogChat.DialogType), así que después de elegir Chat
    // en la rueda no había cómo volver a abrirla para el teclado virtual, y
    // con un cofre abierto no se podía abrir el inventario si estaba en la
    // rueda (reportado en ModDB). Ahora el toque sigue siendo del cursor y
    // mantenerlo abre la rueda.
    //
    // El teclado virtual se queda con el D-pad entero: ahí ↑ sube de fila y
    // mantenerlo para llegar arriba es natural, así que un hold no puede
    // significar otra cosa.
    internal static WheelOpening OpeningFor(GamepadButton openButton,
                                            bool menuOpen, bool keyboardOpen)
    {
        if (!ButtonMapper.IsDPad(openButton)) return WheelOpening.OnPress;
        if (keyboardOpen) return WheelOpening.Never;
        return menuOpen ? WheelOpening.OnHold : WheelOpening.OnPress;
    }

    // Un paso de la apertura: la espera nueva, y si hay que abrir en este tick.
    // Estática y pura para poder probarla sin el juego (gpclab selftest).
    //
    // La rueda sólo sale de un FLANCO del botón, como siempre: un botón que ya
    // venía apretado (por ejemplo desde el teclado virtual, o desde el hold que
    // acaba de confirmar un slot) no la abre aunque cambie el contexto. Una
    // espera en curso sí sobrevive a que el menú se cierre: el usuario la está
    // manteniendo justamente para abrirla.
    internal static (float? Waited, bool Open) StepOpening(
        float? waited, bool down, bool wasDown, WheelOpening opening, float dt)
    {
        // Soltar el botón, o un contexto donde la rueda no puede abrirse,
        // cancela la espera: después hace falta otro flanco.
        if (!down || opening == WheelOpening.Never) return (null, false);

        if (!wasDown)
            return opening == WheelOpening.OnPress ? (null, true) : (0f, false);

        if (waited is not float w) return (null, false);
        w += dt;
        return w >= HoldToOpen ? (null, true) : (w, false);
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
