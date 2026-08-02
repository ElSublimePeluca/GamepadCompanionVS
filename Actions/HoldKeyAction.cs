using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.Client.NoObf;

namespace GamepadCompanion.Actions;

// Mantiene una tecla apretada mientras el botón del gamepad esté apretado.
//
// KeyPressAction manda down+up en el mismo frame ("tap"), que alcanza para
// hotkeys que reaccionan en el down. No alcanza para teclas MODIFICADORAS:
// mods como RKN Crafting leen el estado de la tecla (Alt por default) en el
// momento del click, así que la tecla tiene que estar viva durante el click
// del gatillo. Reportado por pngwn: Alt ni siquiera se podía asignar, y
// remapear el modificador de RKN a "E" tampoco servía porque el tap ya había
// soltado la tecla cuando llegaba el click.
//
// Va por ClientMain.OnKeyDown/OnKeyUp (el pipeline real de teclado) en vez de
// escribir KeyboardState a mano: así se actualizan state Y raw, y los mods que
// escuchan eventos de tecla o tienen hotkeys con modificadores ven lo mismo
// que con un teclado físico. OnKeyUp limpia ambos arrays antes que nada, así
// que el release siempre destraba aunque alguien se coma el evento.
//
// El release lo maneja ButtonMapper por edge de release + ReleaseHolds() en
// todos los caminos donde el driver deja de procesar botones (radial, teclado
// virtual, pérdida de foco, gamepad desconectado). Una tecla modificadora
// latcheada es exactamente la clase de bug del "left trigger curse", así que
// el camino de soltar tiene que ser total.
public sealed class HoldKeyAction : IGameAction, IHoldableAction
{
    public int  KeyCode      { get; }
    public bool CtrlPressed  { get; }
    public bool ShiftPressed { get; }
    public bool AltPressed   { get; }
    public string Label      { get; }

    private bool held;

    // Derivado de `held`, no de KeyCode: si Press abortó porque el mundo no era
    // un ClientMain, la tecla nunca se apretó y no somos dueños de nada.
    public int? HeldKeyCode => held ? KeyCode : null;

    public HoldKeyAction(int keyCode,
                         bool ctrl  = false,
                         bool shift = false,
                         bool alt   = false,
                         string? label = null)
    {
        KeyCode      = keyCode;
        CtrlPressed  = ctrl;
        ShiftPressed = shift;
        AltPressed   = alt;
        Label = label ?? BuildDefaultLabel(keyCode, ctrl, shift, alt);
    }

    public void Press(ICoreClientAPI capi)
    {
        if (held) return;
        if (capi.World is not ClientMain cm) return;
        held = true;
        cm.OnKeyDown(NewEvent());
    }

    public void ReleaseHold(ICoreClientAPI capi)
    {
        if (!held) return;
        held = false;
        if (capi.World is not ClientMain cm) return;
        cm.OnKeyUp(NewEvent());
    }

    // Fallback para consumidores que solo saben ejecutar una vez (la rueda
    // radial): tap, igual que KeyPressAction.
    public void Execute(ICoreClientAPI capi)
    {
        if (capi.World is not ClientMain cm) return;
        cm.OnKeyDown(NewEvent());
        cm.OnKeyUp(NewEvent());
        held = false;
    }

    private KeyEvent NewEvent() => new()
    {
        KeyCode      = KeyCode,
        CtrlPressed  = CtrlPressed,
        ShiftPressed = ShiftPressed,
        AltPressed   = AltPressed,
    };

    private static string BuildDefaultLabel(int keyCode, bool ctrl, bool shift,
                                            bool alt)
    {
        var key = (GlKeys)keyCode;
        var parts = new System.Collections.Generic.List<string>();
        if (ctrl)  parts.Add("Ctrl");
        if (shift) parts.Add("Shift");
        if (alt)   parts.Add("Alt");
        parts.Add(key.ToString());
        return Lang.Get("gamepadcompanion:holdkey-label",
                        string.Join("+", parts));
    }
}
