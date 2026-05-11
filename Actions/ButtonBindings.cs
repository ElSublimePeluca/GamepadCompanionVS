using System.Collections.Generic;
using System.Linq;
using GamepadCompanion.Input;

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

    public static ButtonBindings FromConfig(
        Dictionary<string, SlotConfig?>? config)
    {
        var result = new ButtonBindings();
        if (config is null) return result;

        foreach (var (key, slot) in config)
        {
            if (!System.Enum.TryParse<GamepadButton>(key, out var btn))
                continue;
            var action = SlotConfigToAction(slot);
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

    // Reuso la misma forma de SlotConfig que SlotBindings — tipos
    // hotkey/openDialog/builtin/composite con discriminador.
    private static IGameAction? SlotConfigToAction(SlotConfig? cfg)
    {
        switch (cfg?.Type)
        {
            case "hotkey" when cfg.Code is not null:
                return new HotKeyAction(cfg.Code, cfg.Label ?? cfg.Code);
            case "openDialog" when cfg.DialogType is not null:
                return new OpenLoadedGuiAction(cfg.DialogType,
                                               cfg.Label ?? cfg.DialogType);
            case "builtin" when cfg.Code is not null:
                return new BuiltinAction(cfg.Code, cfg.Label);
            case "keypress" when cfg.KeyCode is int kc:
                return new KeyPressAction(kc,
                    cfg.CtrlPressed, cfg.ShiftPressed, cfg.AltPressed,
                    cfg.Label);
            case "composite" when cfg.Children is not null:
                var children = new List<IGameAction>();
                foreach (var sub in cfg.Children)
                {
                    var c = SlotConfigToAction(sub);
                    if (c is not null and not CompositeAction)
                        children.Add(c);
                }
                return children.Count == 0
                    ? null
                    : new CompositeAction(children, cfg.Label);
            default:
                return null;
        }
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
