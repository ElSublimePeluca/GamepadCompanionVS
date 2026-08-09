using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client.NoObf;

namespace GamepadCompanion.Input;

// RT → click izquierdo (atacar/romper), LT → click derecho (interactuar/colocar),
// vía ClientMain.UpdateMouseButtonState con edge-trigger.
// El layout es invertido respecto al "estándar" de gamepad porque calza mejor
// con el reflejo del usuario para esta mecánica.
//
// Probado y descartado: EntityControls.LeftMouseDown / RightMouseDown — esos
// solo disparan la animación del personaje (swing del brazo) pero el sistema
// de interacciones in-world no los lee. Quien consume es
// SystemMouseInWorldInteractions, que lee de ClientMain.InWorldMouseState
// (campo público, MouseButtonState con bool Left/Middle/Right).
//
// Hasta v1.7.1 escribíamos InWorldMouseState DIRECTO. Funcionaba para el
// gameplay vanilla básico, pero se saltea los eventos MouseDown/MouseUp — y
// tanto el engine como varios mods los usan como señal de "el click terminó":
//
//   - Vanilla `BlockGroundStorage` tiene un latch ESTÁTICO
//     `IsUsingContainedBlock` que se prende al interactuar con un item
//     contenido en ground storage (mineral, cuenco, olla, bolsa, linterna...)
//     y cuyo ÚNICO reset es un handler suscripto a `Event.MouseUp`. Sin
//     MouseUp el latch queda pegado y `OnBlockInteractStart` de TODO ground
//     storage devuelve false para siempre → no se puede levantar herramientas,
//     crisoles, moldes ni pilas del piso, mientras los bloques normales
//     (puertas, cofres, moldes colocados) siguen andando. Ése era el "left
//     trigger curse" de GingeeMaestro: se "arreglaba" al reiniciar el mundo
//     porque cualquier click físico en los menús limpiaba el latch.
//   - CombatOverhaul latchea igual su acción de ataque y solo la apaga un
//     MouseUp físico → arma que se balancea sola.
//
// Por eso ahora ruteamos por `ClientMain.UpdateMouseButtonState(button, down)`,
// que es el método público con el que el propio engine sintetiza clicks para
// las hotkeys "primarymouse"/"secondarymouse" (SystemHotkeys): dispara
// TriggerMouseDown/Up, los pasa por los ClientSystems y recién ahí escribe
// InWorldMouseState. El click del gamepad queda indistinguible de uno real.
//
// A diferencia de los flags de movimiento (que el engine resetea cada frame y
// se reescriben mientras el botón está apretado), InWorldMouseState es
// persistente. Por eso usamos edge-trigger: down on press, up on release.
//
// Trackeamos wroteLeft/wroteRight para poder soltar los botones si perdemos el
// "permiso" de inyectar in-world (GuiDialog modal se abre mid-press, radial,
// teclado virtual, foco perdido). Si no, ej. LT abre un cofre → MouseGrabbed
// pasa a false pero el engine sigue leyendo InWorldMouseState vía
// mouseWorldInteractAnyway, y como SystemMouseInWorldInteractions polea el
// estado continuamente (no edge-triggered), re-disparaba OnBlockInteractStart
// cada BuildRepeatDelay (~0.25s) → el cofre toggleaba open/close en loop.
public sealed class TriggerMapper
{
    private const float Threshold = 0.5f;

    private readonly ICoreClientAPI capi;
    private readonly ButtonMapper buttons;
    private bool wroteLeft;
    private bool wroteRight;

    // Lo lee ScreenInputMirror.Commit para reproyectar
    // ScreenManager.MouseButtonState cada frame: es el mismo registro con el
    // que ReleaseInto decide si mandar el MouseUp, así que espejo y engine no
    // pueden divergir.
    public bool WroteLeft  => wroteLeft;
    public bool WroteRight => wroteRight;

    public TriggerMapper(ICoreClientAPI capi, ButtonMapper buttons)
    {
        this.capi = capi;
        this.buttons = buttons;
    }

    public void Apply(GamepadState current, GamepadState previous)
    {
        if (capi.World is not ClientMain client) return;
        if (client.InWorldMouseState is null) return;

        bool rtNow  = current.RightTrigger  > Threshold;
        bool rtPrev = previous.RightTrigger > Threshold;
        bool ltNow  = current.LeftTrigger   > Threshold;
        bool ltPrev = previous.LeftTrigger  > Threshold;

        // "Left trigger curse" (trace qdjXAamm): la tecla de togglemousecontrol
        // puede quedar latcheada en ClientMain.KeyboardState — OnFocusChanged
        // limpia el mouse state al perder foco pero NO el teclado, así que el
        // KeyUp que se comió un alt-tab / overlay de Steam no llega nunca.
        // UpdateFreeMouse la XORea cada frame → MouseGrabbed=false permanente
        // sin ningún dialog visible, y los triggers eran la única víctima
        // (cámara y movimiento van por otros canales). Si el usuario aprieta
        // un trigger en ese estado, soltamos la tecla fantasma y el grab
        // vuelve solo. Solo en press edge, y solo si la tecla no tiene dueño
        // (ver TryReclaimMouseGrab): "trigger apretado con el mouse libre" NO
        // implica fantasma — mantener la tecla free-mouse mientras se clickea
        // es un flujo legítimo y frecuente.
        if ((rtNow && !rtPrev) || (ltNow && !ltPrev))
        {
            TryReclaimMouseGrab(client);
        }

        // Mismo gate que SystemMouseInWorldInteractions: el engine procesa
        // interacciones con MouseGrabbed *o* mouseWorldInteractAnyway (mouse
        // libre sin ningún Dialog que prefiera ungrabbed). Gatear solo por
        // MouseGrabbed nos dejaba más estrictos que el engine: en el estado
        // free-mouse el click físico interactuaba pero el gamepad no. Con
        // inventario/cofre abierto ambos flags son false → seguimos sin
        // romper bloques detrás de la GUI, igual que antes.
        if (!capi.Input.MouseGrabbed && !client.mouseWorldInteractAnyway)
        {
            ReleaseInto(client);
            return;
        }

        // Anotamos ANTES de salir al engine. SetButton reentra en handlers de
        // terceros y ClientEventManager.TriggerRenderStage no tiene try/catch:
        // si alguno tira, la excepción se lleva puesto el resto del OnTick y
        // nos dejaría con el botón apretado en el engine y el flag en false,
        // o sea sin nadie que lo suelte. Anotando primero el peor caso es
        // soltar de más, que es el lado seguro.
        if (rtNow != rtPrev)
        {
            wroteLeft = rtNow;
            SetButton(client, EnumMouseButton.Left, rtNow);
        }

        if (ltNow != ltPrev)
        {
            wroteRight = ltNow;
            SetButton(client, EnumMouseButton.Right, ltNow);
        }
    }

    // Click por el pipeline real del engine. En el release forzamos además el
    // flag: si un handler de mod marca el MouseEvent como Handled,
    // UpdateMouseButtonState retorna ANTES de limpiar InWorldMouseState y el
    // botón quedaría trabado en true (cofre en loop). En el press no forzamos
    // nada — que una GUI se coma el click es comportamiento correcto.
    private static void SetButton(ClientMain client, EnumMouseButton button,
                                  bool down)
    {
        // Espejo a ScreenManager.MouseButtonState ANTES de bajar al engine:
        // un click físico lo escribe en ScreenManager.OnMouseDown y recién
        // después despacha hacia ClientMain, así que un mod que polee el
        // estático dentro de su propio handler de MouseDown ve el botón ya
        // apretado. Es una escritura de borde por fidelidad de orden; la que
        // garantiza que no quede latcheado es la reproyección por frame de
        // ScreenInputMirror.Commit.
        ScreenInputMirror.SetMouseEdge(button, down);

        client.UpdateMouseButtonState(button, down);
        if (down) return;

        var mouse = client.InWorldMouseState;
        if (mouse is null) return;
        if (button == EnumMouseButton.Left) mouse.Left = false;
        else                                mouse.Right = false;
    }

    // El heal del curse: solo actúa con el mouse ungrabbed sin dialogs
    // abiertos (la firma exacta del estado fantasma — con un dialog real el
    // ungrab es legítimo) y con la tecla free-mouse figurando presionada.
    // Limpia state y raw: ambos quedan latcheados juntos cuando el KeyUp se
    // pierde, y MovementMapper ORea contra raw así que un raw pegado
    // resucitaría el estado que acabamos de limpiar.
    //
    // Una tecla con DUEÑO no es un fantasma. Dos dueños posibles:
    //  1) Nosotros. El default de togglemousecontrol es GlKeys.AltLeft (5) —
    //     exactamente el keycode que RKN Crafting registra de modificador
    //     (rkncrafting.start). "Mantener Alt en A + LT para craftear" caía
    //     entonces justo en la firma del heal: el press de LT le borraba el
    //     Alt del KeyboardState un instante antes de que
    //     SystemMouseInWorldInteractions llegara a OnBlockInteractStart, y RKN
    //     — que lo lee con IsHotKeyPressed, o sea ClientMain.KeyboardState —
    //     veía un click derecho pelado. Reportado por pngwn: con teclado y
    //     mouse andaba, con el gamepad no. v1.8.0 (HoldKeyAction) creó el
    //     caso; v1.7.1 escribió el heal cuando todavía no existía.
    //  2) Un teclado físico: el usuario sostiene la tecla a mano mientras
    //     aprieta LT del gamepad. El comentario de v1.7.1 asumía que ese
    //     usuario no existía; RKN es justamente el flujo que lo crea.
    // Mirar `raw` no distingue: cuando el KeyUp se pierde, state y raw quedan
    // latcheados juntos. GLFW sí, porque el fantasma es literalmente "el
    // engine cree que está apretada y el OS dice que no".
    private void TryReclaimMouseGrab(ClientMain client)
    {
        if (capi.Input.MouseGrabbed || client.DialogsOpened > 0) return;

        int code = capi.Input.GetHotKeyByCode("togglemousecontrol")
            ?.CurrentMapping?.KeyCode ?? -1;
        var state = client.KeyboardState;
        var raw   = client.KeyboardStateRaw;
        if (state is null || code < 0 || code >= state.Length) return;
        if (!state[code]) return;
        if (buttons.IsHoldingKeyCode(code)) return;
        if (ScreenInputMirror.IsKeyDown(code)) return;

        state[code] = false;
        if (raw is not null && code < raw.Length) raw[code] = false;
        // Mismo heal en el espejo: acá ya sabemos que GLFW dice que la tecla
        // está suelta, y un fantasma en KeyboardKeyState es PERMANENTE (los
        // estáticos de ScreenManager no se reasignan al recargar el mundo).
        ScreenInputMirror.SetKeyEdge(code, down: false);
        capi.Logger.Notification(
            "GamepadCompanion: trigger pressed while mouse was ungrabbed with " +
            "no dialog open and the free-mouse key latched in KeyboardState — " +
            "released the key to reclaim mouse grab");
    }

    // Los pseudo-códigos de mouse de ClientMain.KeyboardState (240+botón).
    // UpdateMouseButtonState NO los escribe — solo lo hace OnMouseDownRaw, o
    // sea el camino del click físico. Consecuencia: capi.Input.IsHotKeyPressed
    // devuelve false para CUALQUIER hotkey bindeada a un botón de mouse cuando
    // el click vino del gamepad, en todos los mods a la vez. Es la misma clase
    // de agujero que el de ScreenManager, una capa más abajo y sobre la API
    // pública y documentada.
    //
    // Proyección por frame (no escritura de borde) por el mismo motivo que el
    // espejo: el array es persistente y no lo limpia nadie al perder foco.
    //
    // Pero acá NO se puede hacer `state[code] = want || botón físico`, como sí
    // se hace con los estáticos de ScreenManager. Esos son un reflejo crudo del
    // OS; éste no: OnMouseDownRaw (ClientMain.cs:1951) escribe MouseStateRaw,
    // después recorre los ClientSystems y si alguno tiene CaptureRawMouse()
    // RETORNA — sin escribir KeyboardState[240+botón]. O sea que "botón físico
    // apretado" y "el engine lo anotó" no son lo mismo: un click real comido
    // por una GUI deja el código en false a propósito. Un OR contra GLFW lo
    // pondría en true y estaríamos inventando input del mouse físico.
    //
    // Por eso solo escribimos lo NUESTRO y llevamos la cuenta de si lo forzamos
    // (forcedLeft/forcedRight). Al soltar: si el botón físico está suelto,
    // limpiamos; si está apretado, no tocamos nada y dejamos que el
    // OnMouseUpRaw real ponga el valor que corresponda.
    private bool forcedLeftCode;
    private bool forcedRightCode;

    public void ProjectMouseKeyCodes(bool injecting)
    {
        bool wantLeft  = injecting && wroteLeft;
        bool wantRight = injecting && wroteRight;

        // Ni queremos forzar nada ni hay nada nuestro que deshacer: el array es
        // del engine, no lo tocamos.
        if (!wantLeft && !wantRight && !forcedLeftCode && !forcedRightCode)
            return;

        if (capi.World is not ClientMain client) return;
        var state = client.KeyboardState;
        if (state is null) return;

        forcedLeftCode  = Project(state, EnumMouseButton.Left,
                                  wantLeft,  forcedLeftCode);
        forcedRightCode = Project(state, EnumMouseButton.Right,
                                  wantRight, forcedRightCode);

        static bool Project(bool[] state, EnumMouseButton button,
                            bool want, bool forced)
        {
            int code = KeyCombination.MouseStart + (int)button;
            if (code < 0 || code >= state.Length) return false;
            if (want)
            {
                state[code] = true;
                return true;
            }
            if (!forced) return false;
            if (!ScreenInputMirror.IsMouseDown(button)) state[code] = false;
            return false;
        }
    }

    // Para que el driver lo llame en branches donde Apply se saltea (radial,
    // teclado virtual, modo cursor virtual). Esas dialogs son HUD-type y no
    // ungrabean el mouse, así que el chequeo interno de MouseGrabbed en Apply
    // no las cubriría — necesitamos un release explícito.
    public void Release()
    {
        if (capi.World is not ClientMain client) return;
        ReleaseInto(client);
    }

    // El release SIEMPRE va por el engine, aunque el press haya sido comido
    // por una GUI: un mouse real también manda el up en ese caso, y es
    // justamente el MouseUp lo que destraba los latches de vanilla
    // (ground storage) y de mods (CombatOverhaul).
    private void ReleaseInto(ClientMain client)
    {
        if (wroteLeft)
        {
            SetButton(client, EnumMouseButton.Left, down: false);
            wroteLeft = false;
        }
        if (wroteRight)
        {
            SetButton(client, EnumMouseButton.Right, down: false);
            wroteRight = false;
        }
    }
}
