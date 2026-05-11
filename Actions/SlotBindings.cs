using System;
using System.Linq;
using GamepadCompanion.Input;

namespace GamepadCompanion.Actions;

// Array fijo de 8 slots con la acción asignada a cada uno (puede ser null).
// Mutación in-memory; el ModSystem persiste a JSON tras cada cambio vía
// callback en ConfigDialog.
public sealed class SlotBindings
{
    public const int SlotCount = 12;

    private readonly IGameAction?[] slots;

    public SlotBindings(IGameAction?[] actions)
    {
        if (actions.Length != SlotCount)
            throw new ArgumentException(
                $"SlotBindings requires exactly {SlotCount} entries, got {actions.Length}");
        slots = (IGameAction?[])actions.Clone();
    }

    public IGameAction? this[int index] =>
        index >= 0 && index < SlotCount ? slots[index] : null;

    public void Set(int index, IGameAction? action)
    {
        if (index < 0 || index >= SlotCount) return;
        slots[index] = action;
    }

    // Layout 12 slots, clockwise desde arriba (slot 0 = N, 3 = E,
    // 6 = S, 9 = W). Acciones principales agrupadas en la mitad superior
    // para que queden visibles sin alejar la vista del centro de la
    // pantalla; la mitad inferior queda libre para que el usuario asigne.
    //
    //   slot 0  (12 o'clock):  Personaje
    //   slot 1  (1 o'clock):   Chat
    //   slot 2  (2 o'clock):   Manual
    //   slot 10 (10 o'clock):  Teclado virtual
    //   slot 11 (11 o'clock):  Configurar   (al lado del teclado)
    public static SlotBindings BuildDefault() => new(new IGameAction?[]
    {
        new HotKeyAction("characterdialog",      "Personaje"),
        new OpenLoadedGuiAction("HudDialogChat", "Chat"),
        new HotKeyAction("handbook",             "Manual"),
        null, null, null, null, null, null, null,
        new BuiltinAction("openKeyboard",        "Teclado virtual"),
        new HotKeyAction("gpcompanionconfig",    "Configurar"),
    });

    // Construye SlotBindings desde la config persistida. Si la config es
    // null/inválida, devuelve los defaults (= primer arranque o reset).
    public static SlotBindings FromConfig(SlotConfig?[]? config)
    {
        if (config is null || config.Length != SlotCount)
            return BuildDefault();

        var actions = new IGameAction?[SlotCount];
        for (int i = 0; i < SlotCount; i++)
            actions[i] = ConfigToAction(config[i]);
        return new SlotBindings(actions);
    }

    public SlotConfig?[] ToConfig()
    {
        var arr = new SlotConfig?[SlotCount];
        for (int i = 0; i < SlotCount; i++)
            arr[i] = ActionToConfig(slots[i]);
        return arr;
    }

    private static IGameAction? ConfigToAction(SlotConfig? cfg)
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
                var children = new System.Collections.Generic.List<IGameAction>();
                foreach (var sub in cfg.Children)
                {
                    var c = ConfigToAction(sub);
                    // Anidar composite no es soportado: si alguien edita
                    // el JSON a mano, lo aplanamos descartando.
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

    private static SlotConfig? ActionToConfig(IGameAction? action) =>
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
                           .Select(c => ActionToConfig(c))
                           .ToArray(),
                   },
            _   => null,
        };
}
