namespace GamepadCompanion.Glyphs;

using System;
using Vintagestory.API.Common;

// Cada superficie donde se sustituyen glifos es un canal independiente, con su
// propio disyuntor. Que se caiga uno no apaga los otros: si un update del juego
// renombra `drawHelp`, el cartel vuelve a teclado y el manual sigue con glifos.
//
// El estado se muestra entero en `.gpglyphs`, con el motivo. Para un mod cuyo
// canal de soporte son issues y comentarios de ModDB, poder pedir una sola
// salida y saber qué se cayó y por qué vale más que cualquier log.
internal enum GlyphChannelState
{
    Pending,      // todavía no se intentó (etapa no implementada)
    Active,
    Disabled,     // apagado a mano por el usuario (GlyphChannelsOff)
    Unavailable,  // no se pudo enganchar: firma cambiada, self-test fallido…
    Broken,       // se enganchó pero se rompió en ejecución (disyuntor abierto)
}

internal sealed class GlyphChannel
{
    // Tres fallas y se apaga. El delegate corre DENTRO de la composición de un
    // HUD del juego: mejor perder los glifos que loguear una excepción por
    // frame durante toda la sesión.
    private const int FailureBudget = 3;

    private readonly ILogger logger;
    private int failures;

    public string Name { get; }
    public GlyphChannelState State { get; private set; }
    public string? Reason { get; private set; }

    // Algo que el canal funciona pero conviene decir: un self-test que no se pudo
    // correr, otro mod con un parche sobre el mismo método. No apaga nada.
    public string? Note { get; set; }

    public GlyphChannel(string name, ILogger logger, bool disabledByUser)
    {
        Name = name;
        this.logger = logger;
        if (disabledByUser)
        {
            State = GlyphChannelState.Disabled;
            Reason = "apagado en la config (GlyphChannelsOff)";
        }
    }

    public bool Usable => State == GlyphChannelState.Active;

    public void MarkActive()
    {
        // Un canal que el usuario apagó no se enciende solo.
        if (State == GlyphChannelState.Disabled) return;
        State = GlyphChannelState.Active;
        Reason = null;
        failures = 0;
    }

    public void MarkUnavailable(string why)
    {
        if (State == GlyphChannelState.Disabled) return;
        State = GlyphChannelState.Unavailable;
        Reason = why;
        logger.Notification(
            "GamepadCompanion: glyph channel '{0}' unavailable, that surface stays on " +
            "keyboard hints. {1}", Name, why);
    }

    public void ReportFailure(Exception e)
    {
        if (State is GlyphChannelState.Disabled or GlyphChannelState.Broken) return;
        failures++;
        logger.Warning("GamepadCompanion: glyph channel '{0}' failed ({1}/{2}).",
                       Name, failures, FailureBudget);
        logger.Warning(e);
        if (failures < FailureBudget) return;
        State = GlyphChannelState.Broken;
        Reason = $"{FailureBudget} fallas en ejecución: {e.GetType().Name}";
        logger.Warning(
            "GamepadCompanion: glyph channel '{0}' disabled for this session after {1} " +
            "failures. Everything else keeps working.", Name, FailureBudget);
    }

    // Renglón para `.gpglyphs`.
    public string Describe()
    {
        string state = State switch
        {
            GlyphChannelState.Active      => "activo",
            GlyphChannelState.Pending     => "no aplicado",
            GlyphChannelState.Disabled    => "apagado por el usuario",
            GlyphChannelState.Unavailable => "no disponible — " + (Reason ?? "?"),
            _                             => "roto — " + (Reason ?? "?"),
        };
        return Note is null ? state : state + "  (" + Note + ")";
    }
}
