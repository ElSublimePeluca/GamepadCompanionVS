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
        cursorClicks = new CursorClickMapper(capi, cursor);
        worldMapZoom = new WorldMapZoomMapper(capi);
    }

    public void OnTick(GamepadState current, GamepadState previous, float dt)
    {
        if (!current.IsConnected)
        {
            movement.Release();
            buttons.ReleaseHolds();
            return;
        }

        // Sin foco de ventana (alt-tab) no inyectamos NADA al juego ni al mouse
        // del OS. Antes el cursor virtual seguía snapeando el mouse a la posición
        // de VS aunque estuviera en segundo plano (reportado por ElSublimePeluca).
        if (!IsWindowFocused())
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
        // RB held = modo smooth: stick derecho mueve continuo + RT/LT clickean.
        // RB suelto = modo slot: DPad salta cursor por pasos del tamaño de un
        // slot de inventario, ideal para navegar inventario/cofres sin arrastrar
        // con el stick. RT/LT siguen clickeando en ambos modos.
        bool smoothMode = current.IsDown(GamepadButton.RightBumper);
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
            // navegando con step para que el cursor pueda alcanzar waypoints
            // o botones de UI del mapa.
            bool worldMapZooming = worldMapZoom.Apply(current, previous);
            if (smoothMode)
            {
                cursor.Update(current.RightStickX, current.RightStickY, dt,
                              fw, fh);
            }
            else
            {
                ApplyDPadStep(current, previous, fw, fh,
                              skipVertical: worldMapZooming);
                // En step mode el cursor no se mueve entre pulsos del DPad,
                // pero seguimos sincronizando OS/ClientMain por frame para
                // que el render del item arrastrado en HudDropItem no se
                // quede pegado a la última posición del mouse físico.
                cursor.Sync();
            }
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
    }

    // Tamaño del salto del cursor con DPad. ~52 px coincide con el ancho
    // típico de un slot de inventario en VS (incluyendo bordes), así
    // moverse en horizontal salta exactamente al slot adyacente. Vertical
    // usa el mismo valor — la grilla del inventario es uniforme.
    private const int SlotStepPx = 52;

    private void ApplyDPadStep(GamepadState current, GamepadState previous,
                               int fw, int fh, bool skipVertical = false)
    {
        if (current.WasPressed(GamepadButton.DPadLeft, previous))
            cursor.Step(-SlotStepPx, 0, fw, fh);
        if (current.WasPressed(GamepadButton.DPadRight, previous))
            cursor.Step(+SlotStepPx, 0, fw, fh);
        if (skipVertical) return;
        if (current.WasPressed(GamepadButton.DPadUp, previous))
            cursor.Step(0, -SlotStepPx, fw, fh);
        if (current.WasPressed(GamepadButton.DPadDown, previous))
            cursor.Step(0, +SlotStepPx, fw, fh);
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
