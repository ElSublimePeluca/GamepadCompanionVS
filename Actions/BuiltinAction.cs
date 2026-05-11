using Vintagestory.API.Client;

namespace GamepadCompanion.Actions;

// Acción "built-in" del mod: comportamientos que no son hotkey ni abrir
// dialog, sino mecánicas propias (soltar item, cambiar slot del hotbar,
// cerrar dialog modal, etc.). El Code identifica la implementación en
// BuiltinActions; el Label es texto amigable mostrado en la UI y el
// radial.
//
// Códigos soportados (sincronizado con BuiltinAction.Execute):
//   "dropItem"       - soltar el item activo del hotbar
//   "dropOrDismiss"  - cerrar dialog modal abierto, sino soltar item
//   "hotbarPrev"     - slot anterior del hotbar
//   "hotbarNext"     - slot siguiente del hotbar
public sealed class BuiltinAction : IGameAction
{
    public string Code { get; }
    public string Label { get; }

    public BuiltinAction(string code, string? label = null)
    {
        Code = code;
        Label = label ?? DefaultLabelFor(code);
    }

    public void Execute(ICoreClientAPI capi)
    {
        switch (Code)
        {
            case "dropItem":       BuiltinActions.DropItem(capi); break;
            case "dropOrDismiss":  BuiltinActions.DropOrDismiss(capi); break;
            case "hotbarPrev":     BuiltinActions.HotbarPrev(capi); break;
            case "hotbarNext":     BuiltinActions.HotbarNext(capi); break;
            case "openKeyboard":   BuiltinActions.OpenVirtualKeyboard(capi); break;
            default:
                capi.Logger.Warning(
                    $"GamepadCompanion: builtin action '{Code}' no implementada");
                break;
        }
    }

    public static readonly (string Code, string Label)[] Catalog =
    {
        ("dropItem",      "Soltar item activo"),
        ("dropOrDismiss", "Cerrar dialog / Soltar item"),
        ("hotbarPrev",    "Hotbar: slot anterior"),
        ("hotbarNext",    "Hotbar: slot siguiente"),
        ("openKeyboard",  "Abrir teclado virtual"),
    };

    private static string DefaultLabelFor(string code)
    {
        foreach (var (c, label) in Catalog)
            if (c == code) return label;
        return code;
    }
}
