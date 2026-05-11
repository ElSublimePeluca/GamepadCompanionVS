using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client.NoObf;

namespace GamepadCompanion.Actions;

// Inyecta una pulsación sintética de tecla (down + up) en el engine via
// ClientMain.OnKeyDown/OnKeyUp. Útil para teclas que no están registradas
// como hotkey (movimiento W/A/S/D que el engine procesa via flags) o
// para "sentir" cualquier tecla — incluyendo emotes como G (sit), F5, etc.
//
// El chain interno de OnKeyDown corre todo el pipeline: TriggerKeyDown,
// global hotkeys, ClientSystem.OnKeyDown, TriggerHotKey, y actualiza
// KeyboardState[]/KeyboardStateRaw[]. Por eso una sola llamada cubre
// tanto hotkeys con Handler como movement-like flags.
//
// Down y Up se disparan en el mismo Execute — semántica de "tap". Para
// teclas held (correr sostenido con shift, por ejemplo) eso no alcanza,
// pero la mayoría de hotkeys reaccionan en down. Si en algún caso hace
// falta hold, se puede componer con un Toggle o agregar variantes.
public sealed class KeyPressAction : IGameAction
{
    public int  KeyCode      { get; }
    public bool CtrlPressed  { get; }
    public bool ShiftPressed { get; }
    public bool AltPressed   { get; }
    public string Label      { get; }

    public KeyPressAction(int keyCode,
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

    public void Execute(ICoreClientAPI capi)
    {
        if (capi.World is not ClientMain cm) return;

        var down = new KeyEvent
        {
            KeyCode      = KeyCode,
            CtrlPressed  = CtrlPressed,
            ShiftPressed = ShiftPressed,
            AltPressed   = AltPressed,
        };
        cm.OnKeyDown(down);

        var up = new KeyEvent
        {
            KeyCode      = KeyCode,
            CtrlPressed  = CtrlPressed,
            ShiftPressed = ShiftPressed,
            AltPressed   = AltPressed,
        };
        cm.OnKeyUp(up);
    }

    private static string BuildDefaultLabel(int keyCode, bool ctrl, bool shift, bool alt)
    {
        var key = (GlKeys)keyCode;
        var parts = new System.Collections.Generic.List<string>();
        if (ctrl)  parts.Add("Ctrl");
        if (shift) parts.Add("Shift");
        if (alt)   parts.Add("Alt");
        parts.Add(key.ToString());
        return "Tecla " + string.Join("+", parts);
    }
}
