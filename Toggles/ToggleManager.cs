using GamepadCompanion.Input;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;

namespace GamepadCompanion.Toggles;

// Toggles de Ctrl y Shift accionados con L3 y R3.
//
// El engine de VS separa dos roles para cada modificador:
//   - Sprint / Sneak: estado de movimiento (correr, agacharse). Lo escribimos
//     directo a EntityControls.* (mismo patrón que MovementMapper).
//   - CtrlKey / ShiftKey: rol modificador para clicks (Ctrl+Click, Shift+Click,
//     Ctrl+Shift+RClick para colocar herramienta). EntityControls.CtrlKey/ShiftKey
//     son outputs derivados — el engine los recalcula cada frame leyendo
//     ClientMain.KeyboardState[ControlLeft|ShiftLeft]. Por eso escribir a
//     EntityControls directamente activa la animación/sprint pero no llega al
//     click handler. La fuente autoritativa es el array de KeyboardState.
// Detalle: hacemos OR con KeyboardStateRaw para no pisar la tecla física si el
// usuario también la está apretando (caso teclado + gamepad simultáneo).
//
// Suspensión automática: si MouseGrabbed == false (GuiDialog abierto o ventana
// perdió foco) no proyectamos los flags. El estado del toggle se preserva para
// retomar al volver a gameplay.
public sealed class ToggleManager
{
    private const int KeyShiftLeft   = 1; // GlKeys.ShiftLeft
    private const int KeyControlLeft = 3; // GlKeys.ControlLeft

    private readonly ICoreClientAPI capi;

    public bool CtrlActive { get; private set; }
    public bool ShiftActive { get; private set; }
    public bool PrecisionActive { get; private set; }

    public bool Suspended => !capi.Input.MouseGrabbed;

    // Llamado desde GamepadInputDriver al detectar DPad ↑ en gameplay.
    // Estado público para que ToggleHudOverlay lo muestre como un toggle
    // más junto a CTRL/SHIFT.
    public void TogglePrecision() => PrecisionActive = !PrecisionActive;

    public ToggleManager(ICoreClientAPI capi)
    {
        this.capi = capi;
    }

    public void OnTick(GamepadState current, GamepadState previous)
    {
        if (current.WasPressed(GamepadButton.LeftStick, previous))
            CtrlActive = !CtrlActive;
        if (current.WasPressed(GamepadButton.RightStick, previous))
            ShiftActive = !ShiftActive;

        // KeyboardState SIEMPRE se proyecta — incluso cuando suspendido — para
        // que no quede sticky en `true` al abrir un GuiDialog. El engine solo
        // muta KeyboardState[Shift] en eventos KeyDown/KeyUp reales del SO; si
        // dejáramos de escribir cuando suspended, el último `true` que dejamos
        // sobreviviría y todos los clicks del mouse en inventario serían
        // silenciosamente shift+click. Cuando suspended, projectamos solo el
        // estado físico raw.
        ApplyToKeyboardState();

        if (Suspended) return;

        ApplyToEntityControls();
    }

    private void ApplyToKeyboardState()
    {
        if (capi.World is not ClientMain client) return;

        var state = client.KeyboardState;
        var raw   = client.KeyboardStateRaw;
        if (state is null || raw is null) return;
        if (state.Length <= KeyControlLeft || raw.Length <= KeyControlLeft) return;

        bool projectCtrl  = !Suspended && CtrlActive;
        bool projectShift = !Suspended && ShiftActive;

        // OR del estado físico raw: si la tecla está apretada físicamente nunca
        // la pisamos a false. Cuando toggle on y no suspended, sumamos true.
        // Cuando suspended o toggle off, el array vuelve naturalmente al raw.
        state[KeyControlLeft] = projectCtrl  || raw[KeyControlLeft];
        state[KeyShiftLeft]   = projectShift || raw[KeyShiftLeft];
    }

    private void ApplyToEntityControls()
    {
        var controls = capi.World?.Player?.Entity?.Controls;
        if (controls is null) return;

        // Sprint/Sneak son flags de input del entity, no derivados de KeyboardState.
        // Escribirlos directo es el patrón validado en MovementMapper.
        if (CtrlActive)  controls.Sprint = true;
        if (ShiftActive) controls.Sneak  = true;
    }
}
