using System;
using System.Linq;
using GamepadCompanion.Input;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

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
        new HotKeyAction("characterdialog",
                         Lang.Get("gamepadcompanion:slot-character")),
        new OpenLoadedGuiAction("HudDialogChat",
                         Lang.Get("gamepadcompanion:slot-chat")),
        new HotKeyAction("handbook",
                         Lang.Get("gamepadcompanion:slot-handbook")),
        null, null, null, null, null, null, null,
        new BuiltinAction("openKeyboard",
                         Lang.Get("gamepadcompanion:virtual-keyboard")),
        new HotKeyAction("gpcompanionconfig",
                         Lang.Get("gamepadcompanion:slot-configure")),
    });

    // Construye SlotBindings desde la config persistida. Si la config es
    // null/inválida, devuelve los defaults (= primer arranque o reset).
    // capi se usa para re-resolver labels en el idioma activo (ver
    // SlotConfigActions).
    public static SlotBindings FromConfig(SlotConfig?[]? config,
                                          ICoreClientAPI capi)
    {
        if (config is null || config.Length != SlotCount)
            return BuildDefault();

        var actions = new IGameAction?[SlotCount];
        for (int i = 0; i < SlotCount; i++)
            actions[i] = SlotConfigActions.ToAction(config[i], capi);
        return new SlotBindings(actions);
    }

    public SlotConfig?[] ToConfig()
    {
        var arr = new SlotConfig?[SlotCount];
        for (int i = 0; i < SlotCount; i++)
            arr[i] = ActionToConfig(slots[i]);
        return arr;
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
