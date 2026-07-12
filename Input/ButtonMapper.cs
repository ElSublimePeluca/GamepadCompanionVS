using GamepadCompanion.Actions;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace GamepadCompanion.Input;

// Mapea los botones discretos del gamepad a hotkeys vanilla por edge-trigger.
// D-pad ←/→ se mapean directo a InventoryManager.ActiveHotbarSlotNumber porque
// el juego no expone una hotkey de "next/prev hotbar slot" (solo hotbarslot1..14).
//
// B es contextual: cierra el GuiDialog abierto si hay alguno (chat, inventario,
// pausa, manual, etc), sino dispara `dropitem`. Esto permite navegar dialogs
// con el control sin tocar el teclado — particularmente importante para chat
// (su input field captura Enter/Esc al teclado pero ningún botón de gamepad).
//
// Cada acción edge-press puede ser overrideada por el usuario via ButtonBindings:
// si Bindings[btn] != null, ejecutamos esa IGameAction en lugar del default.
// El "jump mientras se mantiene A" queda unconditional (no se pierde aunque
// remapees A a otra cosa).
public sealed class ButtonMapper
{
    // Default de DPad Down = press G. La tecla G en VS vanilla está
    // bindeada al emote de sentarse (en mods/configs alternativos puede
    // ser otra cosa, pero se queda en la convención del usuario).
    // Propiedad para que el label se resuelva vía Lang en runtime, no en
    // static init (que corre antes de que el Lang del mod esté cargado).
    private static KeyPressAction SitDefault =>
        new((int)Vintagestory.API.Client.GlKeys.G,
            label: Vintagestory.API.Config.Lang.Get("gamepadcompanion:key-g-sit"));

    private readonly ICoreClientAPI capi;
    private readonly HotkeyDispatcher hotkeys;
    private readonly VirtualCursor cursor;

    public ButtonBindings Bindings { get; set; } = ButtonBindings.BuildDefault();

    public ButtonMapper(ICoreClientAPI capi, HotkeyDispatcher hotkeys,
                        VirtualCursor cursor)
    {
        this.capi = capi;
        this.hotkeys = hotkeys;
        this.cursor = cursor;
    }

    public void Apply(GamepadState current, GamepadState previous)
    {
        // A → jump: el engine procesa Jump leyendo el flag de EntityControls,
        // no por Handler. La hotkey "jump" existe en HotKeys (para que sea
        // rebindeable desde el menú) pero su Handler es null. Seteamos el flag
        // mientras A está presionado; el OR con el valor previo respeta lo que
        // haya seteado el teclado en el mismo tick.
        if (current.IsDown(GamepadButton.A))
        {
            var controls = capi.World?.Player?.Entity?.Controls;
            if (controls is not null) controls.Jump = true;
        }

        // Edge-press: cada botón ejecuta el override del user, o si no hay,
        // su default hardcoded.
        if (current.WasPressed(GamepadButton.A,         previous))
            ExecuteOrDefault(GamepadButton.A,         null);
        if (current.WasPressed(GamepadButton.B,         previous))
            ExecuteOrDefault(GamepadButton.B,         DefaultB);
        if (current.WasPressed(GamepadButton.X,         previous))
            ExecuteOrDefault(GamepadButton.X,         () => hotkeys.Trigger("toolmodeselect"));
        if (current.WasPressed(GamepadButton.Y,         previous))
            ExecuteOrDefault(GamepadButton.Y,         () => hotkeys.Trigger("inventorydialog"));
        if (current.WasPressed(GamepadButton.Back,      previous))
            ExecuteOrDefault(GamepadButton.Back,      () => hotkeys.Trigger("worldmapdialog"));
        if (current.WasPressed(GamepadButton.Start,     previous))
            ExecuteOrDefault(GamepadButton.Start,     () => hotkeys.Trigger("escapemenudialog"));
        // DPad defaults se gatean en cursor.Visible: con el cursor virtual
        // activo (dialog modal abierto), DPad navega UI en GamepadInputDriver
        // (step del cursor, zoom del worldmap, etc) — no debe disparar el
        // toggle de precisión, la tecla G, ni cambiar hotbar slot. Si el
        // usuario tiene un override binding para DPad, lo respetamos igual.
        if (current.WasPressed(GamepadButton.DPadUp,    previous))
            ExecuteOrDefault(GamepadButton.DPadUp,    null);
        if (current.WasPressed(GamepadButton.DPadDown,  previous))
            ExecuteOrDefault(GamepadButton.DPadDown,
                cursor.Visible ? null
                               : (System.Action)(() => SitDefault.Execute(capi)));
        if (current.WasPressed(GamepadButton.DPadLeft,  previous))
            ExecuteOrDefault(GamepadButton.DPadLeft,
                cursor.Visible ? null
                               : (System.Action)(() => BuiltinActions.HotbarPrev(capi)));
        if (current.WasPressed(GamepadButton.DPadRight, previous))
            ExecuteOrDefault(GamepadButton.DPadRight,
                cursor.Visible ? null
                               : (System.Action)(() => BuiltinActions.HotbarNext(capi)));
    }

    // Si hay user binding, ejecutalo; si no, corré el default (puede ser
    // null para botones sin default).
    private void ExecuteOrDefault(GamepadButton btn, System.Action? defaultBehavior)
    {
        var userBinding = Bindings[btn];
        if (userBinding is not null)
        {
            userBinding.Execute(capi);
            return;
        }
        defaultBehavior?.Invoke();
    }

    // DefaultB redirige al builtin "dropOrDismiss" — misma lógica que la
    // BuiltinAction expuesta al picker, para mantener un solo punto de
    // verdad del comportamiento.
    private void DefaultB() => BuiltinActions.DropOrDismiss(capi);
}
