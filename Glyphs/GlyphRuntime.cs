namespace GamepadCompanion.Glyphs;

using System;
using System.Collections.Generic;
using GamepadCompanion.Actions;
using GamepadCompanion.Input;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

// Todo lo que necesita el subsistema de glifos para una sesión de mundo.
internal sealed class GlyphSession
{
    // Los bindings entran como funciones y no como el GamepadInputDriver entero,
    // por el mismo motivo que en el resolver: es lo único que se necesita de él,
    // hay que leerlos frescos, y así la sesión se puede construir sin el juego
    // abierto (`gpclab selftest` parchea el engine de verdad y prueba los tres
    // parches offline).
    public GlyphSession(ICoreClientAPI capi, GamepadCompanionConfig config,
                        IGamepadProvider provider,
                        Func<ButtonBindings> buttonBindings, Func<SlotBindings> wheelBindings,
                        string modVersion)
    {
        Capi = capi;
        Logger = capi.Logger;
        Config = config;
        Provider = provider;
        ModVersion = modVersion;
        Resolver = new GlyphResolver(capi, config, provider, buttonBindings, wheelBindings);

        var off = ParseChannelsOff(config.GlyphChannelsOff);
        Icons   = new GlyphChannel("icons",   Logger, off.Contains("icons"));
        Art     = new GlyphChannel("art",     Logger, off.Contains("art"));
        Rewrite = new GlyphChannel("rewrite", Logger, off.Contains("rewrite"));
        Sign    = new GlyphChannel("sign",    Logger, off.Contains("sign"));
        Vtml    = new GlyphChannel("vtml",    Logger, off.Contains("vtml"));
    }

    public ICoreClientAPI Capi { get; }
    public ILogger Logger { get; }
    public GamepadCompanionConfig Config { get; }
    public IGamepadProvider Provider { get; }
    public GlyphResolver Resolver { get; }
    public string ModVersion { get; }

    // Un canal por superficie, cada uno con su disyuntor: que se caiga el cartel
    // no tiene por qué llevarse los íconos de mouse ni el manual.
    // Líneas del cartel que ya se pasaron del techo de ancho en esta sesión.
    // Vuelven a teclado, una por una, sin apagar el canal.
    public HashSet<string> OverflowedLines { get; } = new(System.StringComparer.Ordinal);

    // Lo cablea el ModSystem después de construir la sesión; los parches lo usan
    // para pedir una recomposición cuando degradan una línea.
    public GlyphInvalidator? Invalidator { get; set; }

    public GlyphChannel Icons { get; }     // IconUtil.CustomIcons, sin Harmony
    public GlyphChannel Art { get; }       // los símbolos de las caras de PlayStation
    public GlyphChannel Rewrite { get; }   // sustitución de texto de la tecla
    public GlyphChannel Sign { get; }      // cartel flotante
    public GlyphChannel Vtml { get; }      // tags <hk> en prosa

    public IEnumerable<GlyphChannel> Channels
    {
        get
        {
            yield return Icons; yield return Rewrite; yield return Sign;
            yield return Vtml;  yield return Art;
        }
    }

    private static HashSet<string> ParseChannelsOff(string? value)
    {
        var set = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(value)) return set;
        foreach (string part in value.Split(',', System.StringSplitOptions.RemoveEmptyEntries
                                               | System.StringSplitOptions.TrimEntries))
            set.Add(part);
        return set;
    }
}

// El puente entre el mod y todo lo que corre FUERA de él: el delegate que le
// pasamos a IconUtil.CustomIcons (etapa 2) y, más adelante, los parches de
// Harmony. Ninguno de los dos recibe una referencia nuestra, así que el estado
// tiene que ser alcanzable desde un estático.
//
// Session se pone en null al salir del mundo, y eso NO es opcional: los
// delegates y los parches siguen instalados después del teardown, y una sesión
// que apunta a un ClientMain destruido es la clase exacta de bug irreproducible
// que este repo ya pagó dos veces (el espejo de ScreenManager en la 1.9.0 y el
// latch del joystick fantasma en la 1.10.0).
internal static class GlyphRuntime
{
    internal static GlyphSession? Session;

    // Se limpia desde api.Event.LeaveWorld, que sale de
    // ClientMain.DestroyGameSession y por eso cubre menú Escape, muerte, kick,
    // desconexión y crash del hilo de cliente. Dispose queda como respaldo:
    // si StartClientSide llegó a tirar, el ModSystem desaparece de
    // enabledSystems y Dispose no corre nunca.
    internal static void Clear() => Session = null;
}
