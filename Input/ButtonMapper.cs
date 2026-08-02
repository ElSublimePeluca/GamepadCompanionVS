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
        // Los holds NO se aplican acá: el driver llama a ApplyHolds antes de
        // la etapa de triggers para que un modificador y el click que lo usa
        // puedan salir en el mismo tick (ver comentario en ApplyHolds).

        // A → jump se proyecta en MovementMapper junto con WASD, escribiendo a
        // ClientMain.KeyboardState del hotkey "jump". Antes se seteaba acá el
        // flag EntityControls.Jump: movía al jugador local pero nunca llegaba
        // al servidor, así que el salto no se animaba para los demás. Sigue
        // siendo unconditional (no se pierde aunque remapees A a otra cosa).

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
            // Los holdables ya los maneja ApplyHolds — llamar Execute acá
            // sería un tap extra en cada press.
            if (userBinding is IHoldableAction) return;
            userBinding.Execute(capi);
            return;
        }
        defaultBehavior?.Invoke();
    }

    // Instancia que efectivamente apretamos por botón — no alcanza con mirar
    // Bindings al soltar: el usuario puede reasignar el botón (o recargarse el
    // config entero) con la tecla apretada, y ahí la instancia vieja es la
    // única que sabe que tiene que soltar.
    private readonly System.Collections.Generic.Dictionary<GamepadButton,
        IHoldableAction> heldByButton = new();

    // Bindings "mientras esté apretado" (ej. mantener Alt para el modificador
    // de RKN Crafting): se manejan por edges de press y de release, no por
    // Execute. Las dos mitades están separadas porque el click las quiere en
    // ORDEN OPUESTO, y el driver las llama de los dos lados de triggers.Apply:
    //
    //   press  → tecla ANTES del click: si el usuario aprieta el botón del
    //            modificador y el gatillo en el mismo tick, el click tiene que
    //            salir con la tecla ya apretada. Al revés se pierde justo el
    //            frame que importa (el mod lee el modificador en el MouseDown).
    //   release → tecla DESPUÉS del click: soltando los dos a la vez, el
    //            MouseUp todavía tiene que ver el modificador vivo, igual que
    //            con un teclado donde el dedo del Alt no se levanta primero.
    //
    // Un tick que corta antes (radial, teclado virtual, foco perdido) llama a
    // ReleaseHolds() y no necesita ninguna de las dos.
    public void ApplyHoldPresses(GamepadState current, GamepadState previous)
    {
        foreach (var btn in ButtonBindings.Configurable)
        {
            // Ya lo estamos manteniendo: el release lo decide la otra mitad.
            if (heldByButton.ContainsKey(btn)) continue;
            if (Bindings[btn] is not IHoldableAction binding) continue;
            if (!current.IsDown(btn) || previous.IsDown(btn)) continue;

            // Registrar ANTES de apretar: Press pasa por el pipeline de
            // teclado del engine y dispara handlers de otros mods. Si alguno
            // termina llamando a ReleaseHolds(), tiene que encontrar la
            // entrada — si no, la tecla queda apretada sin dueño que la suelte.
            heldByButton[btn] = binding;
            binding.Press(capi);
        }
    }

    public void ApplyHoldReleases(GamepadState current)
    {
        System.Collections.Generic.List<GamepadButton>? release = null;
        foreach (var (btn, active) in heldByButton)
        {
            // Soltamos si el botón se levantó O si el binding cambió con el
            // botón todavía apretado (reasignar, recargar el config): ahí la
            // instancia vieja es la única que sabe que tiene que soltar. El
            // binding nuevo recién arranca en el próximo press edge — no lo
            // apretamos acá para no heredar una pulsación que no fue suya.
            if (current.IsDown(btn) &&
                ReferenceEquals(active, Bindings[btn] as IHoldableAction))
                continue;

            (release ??= new()).Add(btn);
        }

        if (release is null) return;

        // Soltar recién acá, fuera del foreach: ReleaseHold sale al engine y
        // un handler ajeno podría reentrar y mutar heldByButton, lo que
        // reventaría la iteración. Sacamos la entrada primero para que esa
        // reentrada no vuelva a soltar la misma tecla.
        foreach (var btn in release)
        {
            if (!heldByButton.Remove(btn, out var action)) continue;
            action.ReleaseHold(capi);
        }
    }

    // Soltar todo lo que esté siendo mantenido. Lo llama el driver en cada
    // camino donde deja de procesar botones (gamepad desconectado, ventana sin
    // foco, radial abierto, teclado virtual) y el ModSystem al descargarse:
    // sin esto el edge de release se perdería y la tecla quedaría latcheada.
    public void ReleaseHolds()
    {
        if (heldByButton.Count == 0) return;
        // Vaciar primero, soltar después: mismo motivo que en
        // ApplyHoldReleases — ReleaseHold sale al engine y una reentrada no
        // debe encontrarse iterando un diccionario que estamos mutando.
        var pending =
            new System.Collections.Generic.List<IHoldableAction>(
                heldByButton.Values);
        heldByButton.Clear();
        foreach (var action in pending) action.ReleaseHold(capi);
    }

    // Para el .gptrace: "A:AltLeft+X:LShift", o "-" si no hay ninguna. El
    // trace era ciego a los holds, y eso es exactamente lo que no se podía
    // decidir a distancia en el reporte de RKN Crafting: si el usuario bindeó
    // la entrada de tap o la de mantener, el log se veía igual.
    public string DescribeHolds()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var (btn, action) in heldByButton)
        {
            if (action.HeldKeyCode is not int code) continue;
            if (sb.Length > 0) sb.Append('+');
            sb.Append(btn).Append(':').Append((GlKeys)code);
        }
        return sb.Length == 0 ? "-" : sb.ToString();
    }

    // ¿Hay alguna acción holdable sosteniendo esta tecla ahora mismo? Lo
    // pregunta TriggerMapper antes de "curar" una tecla que cree fantasma.
    // Se resuelve contra heldByButton (no contra Bindings) por la misma razón
    // que el release: es la instancia que efectivamente apretamos, y se vacía
    // sola en ReleaseHolds() en todos los caminos de salida.
    public bool IsHoldingKeyCode(int keyCode)
    {
        if (keyCode < 0) return false;
        foreach (var action in heldByButton.Values)
            if (action.HeldKeyCode == keyCode) return true;
        return false;
    }

    // DefaultB redirige al builtin "dropOrDismiss" — misma lógica que la
    // BuiltinAction expuesta al picker, para mantener un solo punto de
    // verdad del comportamiento.
    private void DefaultB() => BuiltinActions.DropOrDismiss(capi);
}
