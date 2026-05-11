using System.Linq;
using GamepadCompanion.Gui;
using GamepadCompanion.Toggles;
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
        triggers = new TriggerMapper(capi);
        toggles = new ToggleManager(capi);
        radial = new RadialMenuDialog(capi);
        cursorClicks = new CursorClickMapper(capi, cursor);
    }

    public void OnTick(GamepadState current, GamepadState previous, float dt)
    {
        if (!current.IsConnected) return;

        // El radial corre primero. Si está activo, los demás mappers (cámara,
        // botones, triggers, toggles) se saltan: el R stick selecciona slot,
        // B cancela. Movement sigue habilitado a propósito — caminar mientras
        // se elige slot es UX estándar.
        radial.OnGamepadTick(current, previous);

        movement.Apply(current);

        if (radial.IsActive)
        {
            cursor.Hide();
            return;
        }

        // Teclado virtual: cuando está abierto, lo controla todo. DPad
        // navega, A presiona la tecla seleccionada, B cierra. Skipeamos
        // cursor/camera/triggers/buttons para que ningún otro mapper
        // pise el input.
        if (virtualKeyboard is not null && virtualKeyboard.IsOpened())
        {
            cursor.Hide();
            virtualKeyboard.OnGamepadTick(current, previous);
            return;
        }

        // Cursor virtual aparece SIEMPRE que hay un GuiDialog (modal) abierto.
        // RB held = modo smooth: stick derecho mueve continuo + RT/LT clickean.
        // RB suelto = modo slot: DPad salta cursor por pasos del tamaño de un
        // slot de inventario, ideal para navegar inventario/cofres sin arrastrar
        // con el stick. RT/LT siguen clickeando en ambos modos.
        bool cursorActive = AnyModalDialogOpen();
        bool smoothMode   = current.IsDown(GamepadButton.RightBumper);
        if (cursorActive)
        {
            int fw = capi.Render.FrameWidth;
            int fh = capi.Render.FrameHeight;
            cursor.Show(fw, fh);
            if (smoothMode)
            {
                cursor.Update(current.RightStickX, current.RightStickY, dt,
                              fw, fh);
            }
            else
            {
                ApplyDPadStep(current, previous, fw, fh);
                // En step mode el cursor no se mueve entre pulsos del DPad,
                // pero seguimos sincronizando OS/ClientMain por frame para
                // que el render del item arrastrado en HudDropItem no se
                // quede pegado a la última posición del mouse físico.
                cursor.Sync();
            }
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
            triggers.Apply(current, previous);
        }

        buttons.Apply(current, previous);
        toggles.OnTick(current, previous);
    }

    // Tamaño del salto del cursor con DPad. ~52 px coincide con el ancho
    // típico de un slot de inventario en VS (incluyendo bordes), así
    // moverse en horizontal salta exactamente al slot adyacente. Vertical
    // usa el mismo valor — la grilla del inventario es uniforme.
    private const int SlotStepPx = 52;

    private void ApplyDPadStep(GamepadState current, GamepadState previous,
                               int fw, int fh)
    {
        if (current.WasPressed(GamepadButton.DPadLeft, previous))
            cursor.Step(-SlotStepPx, 0, fw, fh);
        if (current.WasPressed(GamepadButton.DPadRight, previous))
            cursor.Step(+SlotStepPx, 0, fw, fh);
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
}
