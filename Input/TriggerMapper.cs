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
    private bool wroteLeft;
    private bool wroteRight;

    public TriggerMapper(ICoreClientAPI capi)
    {
        this.capi = capi;
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
        // un trigger en ese estado, es inequívoco que quiere la acción
        // in-world: soltamos la tecla fantasma y el grab vuelve solo. Solo en
        // press edge para no pelearle a un usuario de teclado que sostiene la
        // tecla a propósito (ese no está a la vez apretando LT del gamepad).
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

        if (rtNow != rtPrev)
        {
            SetButton(client, EnumMouseButton.Left, rtNow);
            wroteLeft = rtNow;
        }

        if (ltNow != ltPrev)
        {
            SetButton(client, EnumMouseButton.Right, ltNow);
            wroteRight = ltNow;
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
    private void TryReclaimMouseGrab(ClientMain client)
    {
        if (capi.Input.MouseGrabbed || client.DialogsOpened > 0) return;

        int code = capi.Input.GetHotKeyByCode("togglemousecontrol")
            ?.CurrentMapping?.KeyCode ?? -1;
        var state = client.KeyboardState;
        var raw   = client.KeyboardStateRaw;
        if (state is null || code < 0 || code >= state.Length) return;
        if (!state[code]) return;

        state[code] = false;
        if (raw is not null && code < raw.Length) raw[code] = false;
        capi.Logger.Notification(
            "GamepadCompanion: trigger pressed while mouse was ungrabbed with " +
            "no dialog open and the free-mouse key latched in KeyboardState — " +
            "released the key to reclaim mouse grab");
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
