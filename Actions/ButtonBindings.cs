using System.Collections.Generic;
using System.Linq;
using GamepadCompanion.Input;
using Vintagestory.API.Client;

namespace GamepadCompanion.Actions;

// Map de GamepadButton → IGameAction? para overridear el comportamiento
// edge-press de cada botón. Una entry null (o ausente) significa "usar
// el default hardcoded de ButtonMapper" para ese botón.
//
// Botones excluidos a propósito:
//   - LB: abre el radial.
//   - RB: activa el modo cursor virtual.
//   - L3/R3: ToggleManager los usa incondicionalmente para Ctrl/Shift
//     toggle; exponer override sería engañoso porque la binding del
//     usuario se sumaría al toggle de Ctrl/Shift en vez de reemplazarlo.
//   - DPad ↑ en gameplay: GamepadInputDriver lo usa para togglear modo
//     precisión. Igual lo dejamos configurable porque la binding fires
//     en paralelo sin pisarse (toggle es silencioso).
public sealed class ButtonBindings
{
    // Orden estable para mostrar en UI. El comentario al lado describe
    // el default actual; queda sincronizado con ButtonMapper.
    public static readonly GamepadButton[] Configurable =
    {
        GamepadButton.A,           // jump (siempre activo) + acción extra
        GamepadButton.B,           // default: dismiss dialog o drop item
        GamepadButton.X,           // default: toolmodeselect
        GamepadButton.Y,           // default: inventorydialog
        GamepadButton.Back,        // default: worldmapdialog
        GamepadButton.Start,       // default: escapemenudialog
        GamepadButton.DPadUp,      // default: nada (+ toggle precisión)
        GamepadButton.DPadDown,    // default: tecla G (sentarse)
        GamepadButton.DPadLeft,    // default: hotbar slot anterior
        GamepadButton.DPadRight,   // default: hotbar slot siguiente
    };

    private readonly Dictionary<GamepadButton, IGameAction?> map = new();

    public IGameAction? this[GamepadButton btn] =>
        map.TryGetValue(btn, out var a) ? a : null;

    public void Set(GamepadButton btn, IGameAction? action)
    {
        if (action is null) map.Remove(btn);
        else map[btn] = action;
    }

    public static ButtonBindings BuildDefault() => new();

    // capi se usa para re-resolver labels en el idioma activo (ver
    // SlotConfigActions).
    public static ButtonBindings FromConfig(
        Dictionary<string, SlotConfig?>? config, ICoreClientAPI capi)
    {
        var result = new ButtonBindings();
        if (config is null) return result;

        foreach (var (key, slot) in config)
        {
            if (!System.Enum.TryParse<GamepadButton>(key, out var btn))
                continue;
            var action = SlotConfigActions.ToAction(slot, capi);
            if (action is not null) result.map[btn] = action;
        }
        return result;
    }

    public Dictionary<string, SlotConfig?> ToConfig()
    {
        var dict = new Dictionary<string, SlotConfig?>();
        foreach (var (btn, action) in map)
        {
            var cfg = ActionToSlotConfig(action);
            if (cfg is not null) dict[btn.ToString()] = cfg;
        }
        return dict;
    }

    private static SlotConfig? ActionToSlotConfig(IGameAction? action) =>
        action switch
        {
            HotKeyAction hk
                => new SlotConfig { Type = "hotkey",
                                    Code = hk.Code, Label = hk.Label },
            OpenLoadedGuiAction og
                => new SlotConfig { Type = "openDialog",
                                    DialogType = og.DialogTypeName,
                                    Label = og.Label },
            BuiltinAction bi
                => new SlotConfig { Type = "builtin",
                                    Code = bi.Code, Label = bi.Label },
            KeyPressAction kp
                => new SlotConfig
                   {
                       Type = "keypress",
                       KeyCode = kp.KeyCode,
                       CtrlPressed = kp.CtrlPressed,
                       ShiftPressed = kp.ShiftPressed,
                       AltPressed = kp.AltPressed,
                       Label = kp.Label,
                   },
            CompositeAction co
                => new SlotConfig
                   {
                       Type = "composite",
                       Label = co.Label,
                       Children = co.Children
                           .Select(c => ActionToSlotConfig(c))
                           .ToArray(),
                   },
            _   => null,
        };
}
