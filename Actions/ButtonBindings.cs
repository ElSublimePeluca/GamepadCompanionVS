using System.Collections.Generic;
using GamepadCompanion.Input;
using Vintagestory.API.Client;

namespace GamepadCompanion.Actions;

// Map de GamepadButton → IGameAction? para overridear el comportamiento
// edge-press de cada botón. Una entry null (o ausente) significa "usar
// el default hardcoded de ButtonMapper" para ese botón.
//
// Botones excluidos a propósito:
//   - LB: abre el radial.
//   - RB: mantenido, hacía que el stick moviera el cursor virtual. Desde el
//     issue #9 el stick lo mueve solo y RB no hace nada; queda afuera hasta
//     decidir si pasa a ser asignable.
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
        GamepadButton.A,           // jump (siempre activo) + acción extra; sin binding, clic en diálogos
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
            // Serializador compartido con SlotBindings (ver SlotConfigActions):
            // tener una copia por dueño de bindings ya costó el bug de los
            // "mantener tecla" que se borraban al guardar la rueda.
            var cfg = SlotConfigActions.ToConfig(action);
            if (cfg is not null) dict[btn.ToString()] = cfg;
        }
        return dict;
    }
}
