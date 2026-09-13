using System.Linq;
using GamepadCompanion.Gui;
using GamepadCompanion.Toggles;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Vintagestory.API.Client;

namespace GamepadCompanion.Input;

// Orquestador del input del gamepad. Recibe el GamepadState por tick y delega
// a los mappers especializados.
public sealed class GamepadInputDriver
{
    private readonly ICoreClientAPI capi;
    private readonly GamepadCompanionConfig config;
    private readonly HotkeyDispatcher hotkeys;
    private readonly ButtonMapper buttons;
    private readonly MovementMapper movement;
    private readonly CameraMapper camera;
    private readonly TriggerMapper triggers;
    private readonly ToggleManager toggles;
    private readonly RadialMenuDialog radial;
    private readonly VirtualCursor cursor;
    private readonly CursorClickMapper cursorClicks;
    private readonly CursorNavigator cursorNavigator;
    private readonly WorldMapZoomMapper worldMapZoom;

    public ToggleManager Toggles => toggles;
    public RadialMenuDialog Radial => radial;
    public VirtualCursor Cursor => cursor;
    public ButtonMapper Buttons => buttons;

    // Teclado virtual on-screen. Cuando está abierto, el driver routea
    // todo el gamepad input al dialog (DPad navega, A presiona, B cierra)
    // y skipea las demás capas de mapeo para no superponer acciones.
    private VirtualKeyboardDialog? virtualKeyboard;
    public VirtualKeyboardDialog VirtualKeyboard =>
        virtualKeyboard ??= new VirtualKeyboardDialog(capi);

    public GamepadInputDriver(ICoreClientAPI capi, GamepadCompanionConfig config)
    {
        this.capi = capi;
        this.config = config;
        hotkeys = new HotkeyDispatcher(capi);
        cursor = new VirtualCursor(capi);
        buttons = new ButtonMapper(capi, hotkeys, cursor);
        movement = new MovementMapper(capi);
        camera = new CameraMapper(capi, config);
        triggers = new TriggerMapper(capi, buttons);
        toggles = new ToggleManager(capi);
        radial = new RadialMenuDialog(capi);
        cursorClicks = new CursorClickMapper(capi, cursor, buttons);
        cursorNavigator = new CursorNavigator(capi, cursor);
        worldMapZoom = new WorldMapZoomMapper(capi);
    }

    // El tick real está en OnTickCore. Este wrapper existe por una sola razón:
    // la proyección del estado sintético (ClientMain.KeyboardState de los
    // toggles y los estáticos de ScreenManager) tiene que correr en TODOS los
    // caminos, no en el camino feliz. Antes había cuatro `return` (gamepad
    // desconectado, sin foco, radial, teclado virtual) por encima de
    // toggles.OnTick, y encima ClientEventManager.TriggerRenderStage no tiene
    // try/catch: una excepción de un handler de otro mod, disparada desde
    // adentro de nuestro click sintético, se lleva puesto el resto del tick.
    // Con el commit en un `finally`, todo eso pasa de "estado latcheado" a
    // "se corrige en el frame siguiente".
    public void OnTick(GamepadState current, GamepadState previous, float dt)
    {
        bool injecting = current.IsConnected && IsWindowFocused();
        try
        {
            OnTickCore(current, previous, dt, injecting);
        }
        finally
        {
            toggles.ProjectKeyboardState(injecting);
            triggers.ProjectMouseKeyCodes(injecting);
            ScreenInputMirror.Commit(toggles, triggers, buttons, injecting);
        }
    }

    private void OnTickCore(GamepadState current, GamepadState previous,
                            float dt, bool injecting)
    {
        if (!current.IsConnected)
        {
            movement.Release();
            // triggers/cursor también: si el pad se desenchufa (o se le acaba
            // la batería) con LT apretado, este `return` corre en todos los
            // frames siguientes y el release se vuelve inalcanzable —
            // InWorldMouseState.Right quedaba latcheado hasta el alt-tab.
            triggers.Release();
            buttons.ReleaseHolds();
            cursor.Hide();
            return;
        }

        // Sin foco de ventana (alt-tab) no inyectamos NADA al juego ni al mouse
        // del OS. Antes el cursor virtual seguía snapeando el mouse a la posición
        // de VS aunque estuviera en segundo plano (reportado por ElSublimePeluca).
        if (!injecting)
        {
            movement.Release();
            triggers.Release();
            buttons.ReleaseHolds();
            cursor.Hide();
            return;
        }

        // Swap opcional de triggers (config): algunos controles reportan LT/RT
        // en orden distinto, o el usuario prefiere el reflejo invertido. Se
        // aplica acá, antes de todos los mappers, para que afecte por igual a
        // los triggers in-world y a los clicks del cursor virtual.
        if (config.SwapTriggers)
        {
            current  = current.WithSwappedTriggers();
            previous = previous.WithSwappedTriggers();
        }

        // Con el juego pausado (menú Escape) el render — y por ende este
        // OnTick — sigue corriendo, pero escribir flags a EntityControls
        // dispara TriggerInWorldAction. Mods de combate como CombatOverhaul
        // (overhaulliblegacycompat) enganchan esa acción y llaman
        // RegisterCallback, prohibido en pausa → crash con developermode on.
        // Movement y triggers son los únicos mappers que emiten acciones de
        // mundo, así que se gatean; cursor/radial/cámara siguen activos para
        // poder navegar menús con el control en pausa.
        bool paused = capi.IsGamePaused;

        // El radial corre primero. Si está activo, los demás mappers (cámara,
        // botones, triggers, toggles) se saltan: el R stick selecciona slot,
        // B cancela. Movement sigue habilitado a propósito — caminar mientras
        // se elige slot es UX estándar.
        radial.OnGamepadTick(current, previous);

        bool vkbdOpen     = virtualKeyboard is not null && virtualKeyboard.IsOpened();
        bool cursorActive = AnyModalDialogOpen();

        // Movement se proyecta SIEMPRE, aunque el resultado sea "ninguna tecla":
        // MovementMapper escribe a ClientMain.KeyboardState, que es persistente,
        // así que saltearse un tick dejaría la última tecla sticky. Caminar
        // mientras se elige slot en el radial es UX estándar y se mantiene; el
        // salto sí se corta cuando A significa otra cosa (teclado virtual) o
        // cuando el engine no lo aceptaría igual (dialog abierto).
        movement.Apply(current,
                       allowMove: !paused && !vkbdOpen,
                       allowJump: !paused && !vkbdOpen && !radial.IsActive
                                  && !cursorActive);

        if (radial.IsActive)
        {
            triggers.Release();
            buttons.ReleaseHolds();
            cursor.Hide();
            return;
        }

        // Teclado virtual: cuando está abierto, lo controla todo. DPad
        // navega, A presiona la tecla seleccionada, B cierra. Skipeamos
        // cursor/camera/triggers/buttons para que ningún otro mapper
        // pise el input.
        if (vkbdOpen)
        {
            triggers.Release();
            buttons.ReleaseHolds();
            cursor.Hide();
            virtualKeyboard!.OnGamepadTick(current, previous);
            return;
        }

        // Teclas mantenidas: el press va ANTES de la etapa de clicks para que
        // "mantener el modificador + gatillo" funcione aunque los dos edges
        // caigan en el mismo tick (el mod que lee el modificador lo lee en el
        // MouseDown). El release va después, ver ButtonMapper.ApplyHoldPresses.
        buttons.ApplyHoldPresses(current, previous);

        // Cursor virtual aparece SIEMPRE que hay un GuiDialog (modal) abierto.
        // Stick derecho: lo mueve libre, como un mouse. DPad: salta al slot,
        // celda o botón más cercano en esa dirección. RT o A hacen click
        // izquierdo (un solo botón, ver CursorClickMapper) y LT el derecho.
        //
        // Hasta el issue #9 el stick sólo lo movía con RB mantenido y el DPad
        // saltaba 52 px a ciegas. Por qué existía ese RB, y por qué ya no hace
        // falta, está en CursorNavigator.
        if (cursorActive)
        {
            // Si abrimos la dialog con LT mid-press (ej. cofre), el
            // press de LT había escrito InWorldMouseState.Right=true.
            // Hay que soltarlo o el engine re-dispara la interacción
            // y el cofre toggleaba open/close en loop.
            triggers.Release();
            int fw = capi.Render.FrameWidth;
            int fh = capi.Render.FrameHeight;
            cursor.Show(fw, fh);
            // WorldMap (full-screen): DPad↑/↓ hacen zoom emitiendo MouseWheel
            // al dialog en vez de mover el cursor virtual. DPad←/→ siguen
            // con el paso fijo para que el cursor pueda recorrer el mapa y
            // alcanzar waypoints.
            bool worldMapZooming = worldMapZoom.Apply(current, previous);
            bool moved = cursor.Update(current.RightStickX, current.RightStickY,
                                       config.Deadzone, dt, fw, fh);
            moved |= cursorNavigator.Apply(current, previous, fw, fh,
                                           worldMap: worldMapZooming);
            // En un frame sin movimiento seguimos sincronizando OS/ClientMain
            // para que el render del item arrastrado en HudDropItem no se
            // quede pegado a la última posición del mouse físico.
            if (!moved) cursor.Sync();
            // Si el usuario tomó el mouse físico, no inyectamos clicks del
            // gamepad: apuntarían a la posición (vieja) del cursor virtual.
            // El mouse físico y sus botones manejan el dialog.
            if (!cursor.PhysicalOverride)
                cursorClicks.Apply(current, previous);
        }
        else
        {
            cursor.Hide();

            // DPad ↑ en gameplay togglea modo precisión. Hardcodeado acá
            // (no pasa por ButtonMapper) porque no es una acción discreta
            // sino un modificador continuo de la cámara — toggle stateful.
            // El estado vive en ToggleManager para que ToggleHudOverlay lo
            // muestre junto a CTRL/SHIFT en la esquina superior derecha.
            if (current.WasPressed(GamepadButton.DPadUp, previous))
            {
                toggles.TogglePrecision();
            }

            float factor = toggles.PrecisionActive ? config.PrecisionFactor : 1f;
            camera.Apply(current, dt, factor);
            if (paused) triggers.Release();
            else triggers.Apply(current, previous);
        }

        buttons.Apply(current, previous);
        buttons.ApplyHoldReleases(current);
        toggles.OnTick(current, previous);
        // Ojo: la proyección a KeyboardState / ScreenManager NO va acá — va en
        // el `finally` de OnTick, que es el único punto que alcanzan también
        // los cuatro `return` de arriba.
    }

    // Soltar todo lo que estemos inyectando, pasando por el engine. Lo llama
    // el ModSystem desde Event.LeaveWorld: es el último instante en que el
    // ClientMain todavía acepta input sintético (TriggerLeaveWorld corre al
    // principio de DestroyGameSession, antes de MouseGrabbed=false y antes de
    // Dispose(), que es lo que pone `disposed = true` y convierte a
    // OnKeyUp/OnMouseUp en no-ops silenciosos). Acá es donde el MouseUp
    // todavía destraba los latches de vanilla (ground storage) y de mods
    // (CombatOverhaul).
    public void ReleaseAll()
    {
        movement.Release();
        triggers.Release();
        buttons.ReleaseHolds();
        cursor.Hide();
        toggles.ProjectKeyboardState(injecting: false);
        triggers.ProjectMouseKeyCodes(injecting: false);
    }

    private bool AnyModalDialogOpen()
    {
        return capi.Gui.OpenedGuis.Any(d =>
            d is not null && d.IsOpened() &&
            d.DialogType == EnumDialogType.Dialog);
    }

    // El contexto GL está current en el hilo de render donde corre OnTick, así
    // que GetCurrentContext devuelve la ventana de VS. window==null (edge o
    // headless) → asumimos foco para no romper.
    private static unsafe bool IsWindowFocused()
    {
        var window = GLFW.GetCurrentContext();
        return window == null
            || GLFW.GetWindowAttrib(window, WindowAttributeGetBool.Focused);
    }
}
