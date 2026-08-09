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

    // Lo que efectivamente proyectamos este frame (toggle on, no suspendido y
    // el driver inyectando). Lo lee ScreenInputMirror en el commit: el espejo
    // tiene que reflejar lo que escribimos, no lo que el toggle "quisiera".
    public bool ProjectedCtrl  { get; private set; }
    public bool ProjectedShift { get; private set; }

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

        // La proyección a KeyboardState ya NO se hace acá: la llama el driver
        // en su `finally`, para que corra también en los caminos donde este
        // OnTick ni se alcanza (gamepad desconectado, ventana sin foco, radial
        // abierto, teclado virtual). Antes esos cuatro caminos dejaban el
        // último valor congelado en vez de soltarlo.
        if (Suspended) return;

        ApplyToEntityControls();
    }

    // KeyboardState SIEMPRE se proyecta — incluso cuando suspendido, incluso
    // con el gamepad desenchufado — para que no quede sticky en `true` al
    // abrir un GuiDialog. El engine solo muta KeyboardState[Shift] en eventos
    // KeyDown/KeyUp reales del SO; si dejáramos de escribir cuando suspended,
    // el último `true` que dejamos sobreviviría y todos los clicks del mouse
    // en inventario serían silenciosamente shift+click. Cuando suspended,
    // proyectamos solo el estado físico raw.
    //
    // `injecting` = false es el caso "el mod no está inyectando nada este
    // frame" (sin gamepad, sin foco): baja la proyección a solo-físico sin
    // tocar CtrlActive/ShiftActive, así el toggle se recupera solo al volver.
    //
    // "Siempre" tiene un límite: solo tocamos la tecla que estamos proyectando
    // o que proyectamos el frame anterior y hay que devolver. Escribir
    // `state[K] = raw[K]` cuando no proyectamos nada NO es neutro — state y raw
    // divergen legítimamente: ClientMain.OnKeyDown escribe raw temprano pero
    // state recién al final, después de cuatro early-returns (TriggerKeyDown
    // handled, hotkey global, CaptureAllInputs, ClientSystem que marca
    // Handled). Igualarlos por las nuestras resucitaría teclas que el engine
    // decidió no anotar, y encima lo haríamos 60 veces por segundo aunque el
    // usuario no tenga gamepad enchufado.
    private bool projectedCtrlLast;
    private bool projectedShiftLast;

    public void ProjectKeyboardState(bool injecting)
    {
        ProjectedCtrl  = injecting && !Suspended && CtrlActive;
        ProjectedShift = injecting && !Suspended && ShiftActive;

        // Proyectar o devolver: en los dos casos hay que escribir. Si no es
        // ninguno de los dos, el array no es asunto nuestro.
        bool touchCtrl  = ProjectedCtrl  || projectedCtrlLast;
        bool touchShift = ProjectedShift || projectedShiftLast;
        if (!touchCtrl && !touchShift) return;

        if (capi.World is not ClientMain client) return;

        var state = client.KeyboardState;
        var raw   = client.KeyboardStateRaw;
        if (state is null || raw is null) return;
        if (state.Length <= KeyControlLeft || raw.Length <= KeyControlLeft) return;

        // OR del estado físico raw: si la tecla está apretada físicamente nunca
        // la pisamos a false. Cuando toggle on y no suspended, sumamos true.
        // Cuando suspended o toggle off, el array vuelve naturalmente al raw.
        if (touchCtrl)
            state[KeyControlLeft] = ProjectedCtrl  || raw[KeyControlLeft];
        if (touchShift)
            state[KeyShiftLeft]   = ProjectedShift || raw[KeyShiftLeft];

        // Recién acá, después de que la escritura efectivamente salió: si
        // volvimos por el cast o por un array nulo, seguimos debiendo la
        // devolución y la reintentamos el frame siguiente.
        projectedCtrlLast  = ProjectedCtrl;
        projectedShiftLast = ProjectedShift;
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
