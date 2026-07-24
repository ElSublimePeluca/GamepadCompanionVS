using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;

namespace GamepadCompanion.Input;

// L stick → movimiento. Velocidad fija — para caminar lento se usa el toggle
// de sneak (M3).
//
// **NO escribimos EntityControls.Forward/Backward/... directo.** Escribir ahí
// mueve al jugador localmente pero el servidor nunca se entera, así que los
// demás jugadores ven al personaje deslizándose sin animación de piernas
// (reportado por GingeeMaestro en LAN). El motivo está en
// `Vintagestory.Client.NoObf.SystemPlayerControl.OnGameTick` (tick de 20ms):
//
//     entityControls.Forward = game.KeyboardState[forwardKey];   // ← nos pisa
//     ...
//     SendServerPackets(prevControls, entityControls);           // ← diffea acá
//
// El engine reasigna los flags desde KeyboardState y JUSTO DESPUÉS diffea
// contra el tick anterior para emitir `Packet_MoveKeyChange` (id 21). Nuestro
// `true` se escribe en el render frame, o sea entre dos ticks: en el instante
// del diff el flag ya volvió a false, así que el paquete "forward down" nunca
// sale. El servidor mantiene `ServerControls.Forward = false` y lo replica a
// los demás clientes; `EntityAgent.OnGameTick` calcula `CurrentControls`
// (Idle/Move/Sprint/Sneak → qué animación corre) **solo** desde
// `servercontrols`, de ahí el glide sin animar.
//
// La fuente autoritativa es entonces `ClientMain.KeyboardState`, igual que para
// Ctrl/Shift en ToggleManager. Proyectando ahí, el gamepad se vuelve
// indistinguible del teclado: el engine setea los flags, manda los paquetes y
// aplica sus propios gates (jump requiere MouseGrabbed + PrevFrameCanStandUp).
//
// KeyboardState es persistente (el engine solo lo muta en KeyDown/KeyUp reales),
// así que hay que escribir SIEMPRE — también cuando no queremos moviemiento —
// o el último `true` queda sticky. Por eso `Apply` se llama en todos los paths
// del driver y `Release()` existe para los early returns.
public sealed class MovementMapper
{
    private const float Deadzone           = 0.2f;
    private const float DirectionThreshold = 0.3f;

    private readonly ICoreClientAPI capi;

    public MovementMapper(ICoreClientAPI capi)
    {
        this.capi = capi;
    }

    // Suelta todas las teclas que proyectamos, dejando pasar solo el teclado
    // físico. Para los early returns del driver (sin foco, sin gamepad).
    public void Release() => Project(false, false, false, false, false);

    public void Apply(GamepadState current, bool allowMove, bool allowJump)
    {
        bool fwd = false, back = false, left = false, right = false;

        // FloorSitting bloquea el movimiento: el teclado nunca genera ese
        // estado porque SystemPlayerControl.OnKeyDown limpia `nowFloorSitting`
        // al detectar WASD/jump — pero eso corre en el evento real del SO, que
        // escribir el array no dispara. Sin este gate, sentarse con DPad↓ y
        // mover el stick L hacía caminar al personaje sentado. Si el usuario
        // quiere moverse, presiona DPad↓ otra vez para levantarse.
        var controls = capi.World?.Player?.Entity?.Controls;
        bool seated  = controls?.FloorSitting ?? false;

        if (allowMove && !seated)
        {
            float x = current.LeftStickX;
            float y = current.LeftStickY;

            if (x * x + y * y >= Deadzone * Deadzone)
            {
                fwd   = y >  DirectionThreshold;
                back  = y < -DirectionThreshold;
                left  = x < -DirectionThreshold;
                right = x >  DirectionThreshold;
            }
        }

        Project(fwd, back, left, right,
                allowJump && current.IsDown(GamepadButton.A));
    }

    private void Project(bool fwd, bool back, bool left, bool right, bool jump)
    {
        if (capi.World is not ClientMain client) return;

        var state = client.KeyboardState;
        var raw   = client.KeyboardStateRaw;
        if (state is null || raw is null) return;

        Write(state, raw, "walkforward",  fwd);
        Write(state, raw, "walkbackward", back);
        Write(state, raw, "walkleft",     left);
        Write(state, raw, "walkright",    right);
        Write(state, raw, "jump",         jump);
    }

    // OR con el estado físico raw: si la tecla está apretada en el teclado
    // nunca la pisamos a false (teclado + gamepad simultáneo). Resolvemos el
    // keycode por hotkey code y no hardcodeado, para respetar rebinds.
    private void Write(bool[] state, bool[] raw, string hotkey, bool want)
    {
        int code = capi.Input.GetHotKeyByCode(hotkey)?.CurrentMapping?.KeyCode ?? -1;
        if (code < 0 || code >= state.Length || code >= raw.Length) return;

        state[code] = want || raw[code];
    }
}
