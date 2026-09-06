using System.Collections.Generic;
using System.Linq;
using GamepadCompanion.Input;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace GamepadCompanion.Actions;

// Serialización compartida SlotConfig ⇄ IGameAction, en los dos sentidos. La
// usan SlotBindings (rueda) y ButtonBindings (overrides de botones), que
// persisten la misma forma plana con discriminador.
//
// Los labels NO se toman del snapshot persistido salvo como último recurso:
// se re-resuelven en cada carga para que sigan el idioma activo del cliente.
// El JSON guarda el label solo por legibilidad y como fallback (p.ej. una
// hotkey de otro mod que todavía no se registró cuando cargamos nosotros).
internal static class SlotConfigActions
{
    // Codes con label curado propio del mod: cortos para la rueda, en vez
    // del nombre completo de la hotkey ("gpcompanionconfig" se registra
    // como "GamepadCompanion: ..." — demasiado largo para un slot).
    private static readonly Dictionary<string, string> CuratedHotkeyLabels = new()
    {
        ["characterdialog"]   = "gamepadcompanion:slot-character",
        ["handbook"]          = "gamepadcompanion:slot-handbook",
        ["gpcompanionconfig"] = "gamepadcompanion:slot-configure",
    };

    private static readonly Dictionary<string, string> CuratedDialogLabels = new()
    {
        ["HudDialogChat"] = "gamepadcompanion:slot-chat",
    };

    // "Teclado virtual" (label default de la rueda) en vez del label de
    // catálogo "Abrir teclado virtual", más largo de lo que entra cómodo
    // en un slot del radial.
    private static readonly Dictionary<string, string> CuratedBuiltinLabels = new()
    {
        ["openKeyboard"] = "gamepadcompanion:virtual-keyboard",
    };

    public static IGameAction? ToAction(SlotConfig? cfg, ICoreClientAPI capi)
    {
        switch (cfg?.Type)
        {
            case "hotkey" when cfg.Code is not null:
                return new HotKeyAction(cfg.Code, HotkeyLabel(cfg, capi));
            case "openDialog" when cfg.DialogType is not null:
                return new OpenLoadedGuiAction(cfg.DialogType, DialogLabel(cfg));
            case "builtin" when cfg.Code is not null:
                // Sin label persistido: curado del mod o, con label null,
                // BuiltinAction resuelve del catálogo localizado.
                return new BuiltinAction(cfg.Code,
                    CuratedBuiltinLabels.TryGetValue(cfg.Code, out var bk)
                        ? Lang.Get(bk) : null);
            case "keypress" when cfg.KeyCode is int kc:
                // Ídem: el label "Tecla X" se regenera localizado.
                return new KeyPressAction(kc,
                    cfg.CtrlPressed, cfg.ShiftPressed, cfg.AltPressed);
            case "holdkey" when cfg.KeyCode is int hkc:
                return new HoldKeyAction(hkc,
                    cfg.CtrlPressed, cfg.ShiftPressed, cfg.AltPressed);
            case "composite" when cfg.Children is not null:
                var children = new List<IGameAction>();
                foreach (var sub in cfg.Children)
                {
                    var c = ToAction(sub, capi);
                    // Anidar composite no es soportado: si alguien edita
                    // el JSON a mano, lo aplanamos descartando.
                    if (c is not null and not CompositeAction)
                        children.Add(c);
                }
                // Label = join de los children ya re-resueltos.
                return children.Count == 0
                    ? null
                    : new CompositeAction(children);
            default:
                return null;
        }
    }

    private static string HotkeyLabel(SlotConfig cfg, ICoreClientAPI capi)
    {
        if (CuratedHotkeyLabels.TryGetValue(cfg.Code!, out var key))
            return Lang.Get(key);
        // Nombre vivo de la hotkey — localizado por VS o por el mod dueño.
        // Puede faltar si ese mod carga después que nosotros: snapshot.
        var live = capi.Input.GetHotKeyByCode(cfg.Code!)?.Name;
        return live ?? cfg.Label ?? cfg.Code!;
    }

    private static string DialogLabel(SlotConfig cfg)
    {
        if (CuratedDialogLabels.TryGetValue(cfg.DialogType!, out var key))
            return Lang.Get(key);
        return cfg.Label ?? cfg.DialogType!;
    }

    // El camino de vuelta. Vive acá, y no una copia en cada dueño de bindings,
    // porque las dos copias que había se desincronizaron: la de la rueda nunca
    // aprendió "holdkey", así que un "mantener tecla" asignado a un slot caía
    // en el `_ => null` y se BORRABA al guardar. Y no degradaba a tap: como
    // HoldKeyAction no hereda de KeyPressAction, no lo agarraba ninguna otra
    // rama. Con una sola copia, un tipo de acción nuevo se agrega en un solo
    // lugar — o no anda en ninguno, que al menos se nota.
    //
    // Todo lo que ToAction sepa leer tiene que tener su rama acá; el
    // discriminador `Type` es el mismo string de los dos lados.
    public static SlotConfig? ToConfig(IGameAction? action) =>
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
            HoldKeyAction hd
                => new SlotConfig
                   {
                       Type = "holdkey",
                       KeyCode = hd.KeyCode,
                       CtrlPressed = hd.CtrlPressed,
                       ShiftPressed = hd.ShiftPressed,
                       AltPressed = hd.AltPressed,
                       Label = hd.Label,
                   },
            CompositeAction co
                => new SlotConfig
                   {
                       Type = "composite",
                       Label = co.Label,
                       Children = co.Children
                           .Select(c => ToConfig(c))
                           .ToArray(),
                   },
            _   => null,
        };
}
