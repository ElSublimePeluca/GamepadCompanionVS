using System.Collections.Generic;
using GamepadCompanion.Actions;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace GamepadCompanion.Input;

public enum GamepadLayoutKind
{
    // El de 1.13 y anteriores. Rueda en LB, hotbar en el D-pad ←/→, precisión
    // en D-pad ↑, RB sin uso.
    Classic,
    // Rueda en D-pad ↑, hotbar en los bumpers (con repetición al mantener),
    // precisión en D-pad ←, personaje en D-pad →.
    Modern,
}

// Un layout es el mapa de DEFAULTS de los botones: qué hace cada uno cuando el
// usuario no le asignó nada en la tab Botones, más cuál es el botón que abre la
// rueda.
//
// Existe porque el layout nuevo mueve de lugar la rueda, la navegación del
// hotbar y el modo precisión, y el que venía de 1.13 tiene la memoria muscular
// en el viejo. En vez de romperla, el viejo queda como preset elegible: el mod
// pregunta una vez al actualizar (Gui/LayoutPromptDialog) y después se cambia
// cuando se quiera desde la tab Botones.
//
// **El layout no pisa los bindings del usuario.** ButtonBindings sigue siendo
// la capa de arriba: un botón con binding hace lo del binding en los dos
// layouts, y cambiar de layout sólo cambia los defaults. Por eso cambiarlo no
// puede perder configuración.
//
// Fuente ÚNICA de los defaults: la leen ButtonMapper (para ejecutarlos),
// GlyphResolver (para saber qué tecla mostrar en los hints del juego) y
// ConfigDialog (para escribir "Default (…)" en cada fila). Si mañana X deja de
// ser toolmodeselect, las tres se enteran solas.
public sealed class GamepadLayout
{
    public GamepadLayoutKind Kind { get; }

    // Botón que abre la rueda mientras se lo mantiene. Está RESERVADO: no
    // ejecuta default ni binding, porque el hold ya significa otra cosa. Un
    // binding viejo sobre ese botón (por ejemplo, hotbar en LB asignado en el
    // layout nuevo y después volver al clásico) queda dormido, no se borra.
    public GamepadButton Wheel { get; }

    private readonly Dictionary<GamepadButton, IGameAction> defaults;

    private GamepadLayout(GamepadLayoutKind kind, GamepadButton wheel,
                          Dictionary<GamepadButton, IGameAction> defaults)
    {
        Kind = kind;
        Wheel = wheel;
        this.defaults = defaults;
    }

    public bool IsWheel(GamepadButton button) => button == Wheel;

    public IGameAction? Default(GamepadButton button)
        => !IsWheel(button) && defaults.TryGetValue(button, out var action)
           ? action : null;

    public string Name => NameOf(Kind);

    public static string NameOf(GamepadLayoutKind kind) => Lang.Get(
        kind == GamepadLayoutKind.Classic
            ? "gamepadcompanion:layout-classic"
            : "gamepadcompanion:layout-modern");

    // El default para una instalación NUEVA. Al que ya tenía el mod se le
    // pregunta (ver GamepadCompanionModSystem.ResolveLayout): que un update
    // le cambie los botones sin avisar es justo lo que esto viene a evitar.
    public const GamepadLayoutKind FreshInstall = GamepadLayoutKind.Modern;

    public static string ToCode(GamepadLayoutKind kind)
        => kind == GamepadLayoutKind.Classic ? "classic" : "modern";

    // Parseo tolerante, por el mismo motivo que GlyphStyles.Parse: el campo del
    // JSON es un string y no un enum porque LoadModConfig es un
    // DeserializeObject pelado y un valor desconocido tiraría DENTRO de
    // StartClientSide, llevándose el mod entero.
    public static GamepadLayoutKind Parse(string? code) => code?.ToLowerInvariant() switch
    {
        "classic" => GamepadLayoutKind.Classic,
        "modern"  => GamepadLayoutKind.Modern,
        _         => FreshInstall,
    };

    // capi se usa para ponerle a cada default el nombre VIVO de la hotkey, que
    // es lo que después se lee en "Default (Inventario)". Por eso se construye
    // acá y no en un static readonly: Lang y las hotkeys de los mods no están
    // listos hasta StartClientSide.
    public static GamepadLayout Build(GamepadLayoutKind kind, ICoreClientAPI capi)
        => kind == GamepadLayoutKind.Classic ? Classic(capi) : Modern(capi);

    // Los cuatro botones de cara y los dos centrales son iguales en los dos
    // layouts; lo único que se mueve es la rueda, el hotbar, la precisión y el
    // D-pad →. A no tiene default: salta desde MovementMapper (siempre, aunque
    // se lo remapee) y sin binding además clickea en los diálogos.
    private static Dictionary<GamepadButton, IGameAction> Common(ICoreClientAPI capi) => new()
    {
        [GamepadButton.B]     = Builtin("dropOrDismiss"),
        [GamepadButton.X]     = Hotkey(capi, "toolmodeselect"),
        [GamepadButton.Y]     = Hotkey(capi, "inventorydialog"),
        [GamepadButton.Back]  = Hotkey(capi, "worldmapdialog"),
        [GamepadButton.Start] = Hotkey(capi, "escapemenudialog"),
        [GamepadButton.DPadDown] = Builtin("sitDown"),
    };

    private static GamepadLayout Classic(ICoreClientAPI capi)
    {
        var map = Common(capi);
        map[GamepadButton.DPadUp]    = Builtin("precisionToggle");
        map[GamepadButton.DPadLeft]  = Builtin("hotbarPrev");
        map[GamepadButton.DPadRight] = Builtin("hotbarNext");
        return new GamepadLayout(GamepadLayoutKind.Classic,
                                 GamepadButton.LeftBumper, map);
    }

    private static GamepadLayout Modern(ICoreClientAPI capi)
    {
        var map = Common(capi);
        map[GamepadButton.DPadLeft]   = Builtin("precisionToggle");
        map[GamepadButton.DPadRight]  = Hotkey(capi, "characterdialog");
        map[GamepadButton.LeftBumper] = Builtin("hotbarPrev");
        map[GamepadButton.RightBumper]= Builtin("hotbarNext");
        return new GamepadLayout(GamepadLayoutKind.Modern,
                                 GamepadButton.DPadUp, map);
    }

    private static BuiltinAction Builtin(string code) => new(code);

    // El Name vivo y no el código: es lo que ve el usuario en la fila del
    // botón, y respeta el idioma y los renombres de los mods.
    private static HotKeyAction Hotkey(ICoreClientAPI capi, string code)
        => new(code, capi.Input.GetHotKeyByCode(code)?.Name ?? code);
}
