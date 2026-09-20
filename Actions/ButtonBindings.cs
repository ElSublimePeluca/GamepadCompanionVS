using System.Collections.Generic;
using GamepadCompanion.Input;
using Vintagestory.API.Client;

namespace GamepadCompanion.Actions;

// Map de GamepadButton → IGameAction? con el override del USUARIO para el
// comportamiento edge-press de cada botón. Una entry null (o ausente)
// significa "usar el default del layout activo" (ver GamepadLayout).
//
// Las dos capas son a propósito: el layout dice qué hace un botón de fábrica y
// el usuario puede taparlo botón por botón. Cambiar de layout cambia sólo la
// capa de abajo, así que nunca borra lo que el usuario asignó.
//
// Botones excluidos a propósito:
//   - L3/R3: ToggleManager los usa incondicionalmente para Ctrl/Shift
//     toggle; exponer override sería engañoso porque la binding del
//     usuario se sumaría al toggle de Ctrl/Shift en vez de reemplazarlo.
//   - Guide: no lo reporta la mitad de los mandos y en Steam abre el overlay.
//
// LB y RB SÍ están: hasta 1.13 LB era la rueda y RB no hacía nada, así que
// quedaban afuera. Ahora el botón de la rueda lo decide el layout, y el que le
// toca queda reservado — `Layout.IsWheel` — en vez de estar excluido de la
// lista, para que la fila se siga viendo y se entienda dónde está la rueda.
public sealed class ButtonBindings
{
    // Orden estable para mostrar en UI. Los defaults de cada uno salen del
    // layout, no de acá: ver GamepadLayout.Classic / .Modern.
    public static readonly GamepadButton[] Configurable =
    {
        GamepadButton.A,           // salta siempre (MovementMapper); sin binding, clic en diálogos
        GamepadButton.B,
        GamepadButton.X,
        GamepadButton.Y,
        GamepadButton.LeftBumper,
        GamepadButton.RightBumper,
        GamepadButton.Back,
        GamepadButton.Start,
        GamepadButton.DPadUp,
        GamepadButton.DPadDown,
        GamepadButton.DPadLeft,
        GamepadButton.DPadRight,
    };

    private readonly Dictionary<GamepadButton, IGameAction?> map = new();

    // Layout activo, del que salen los defaults. Lo inyecta el ModSystem
    // después de cargar la config (y lo reemplaza al cambiar de layout).
    // Puede ser null en tests y en el instante entre construir y cablear: ahí
    // Effective degrada al override y listo.
    public GamepadLayout? Layout { get; set; }

    // El override del usuario, sin el default. Es lo que se persiste y lo que
    // muestra la tab Botones como "asignado".
    public IGameAction? this[GamepadButton btn] =>
        map.TryGetValue(btn, out var a) ? a : null;

    // Lo que el botón hace DE VERDAD: override del usuario, o el default del
    // layout. El botón de la rueda no hace ninguna de las dos.
    public IGameAction? Effective(GamepadButton btn)
    {
        if (Layout is not null && Layout.IsWheel(btn)) return null;
        return this[btn] ?? Layout?.Default(btn);
    }

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
