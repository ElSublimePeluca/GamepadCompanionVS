namespace GamepadCompanion.Glyphs;

using System;

// Qué familia de serigrafía se muestra. `Auto` no es un valor que llegue a la
// tabla de etiquetas: el resolver lo resuelve a una familia concreta antes.
public enum GlyphStyle { Off, Auto, Xbox, PlayStation, Nintendo }

internal static class GlyphStyles
{
    // GameSir. Un Cyclone 2 por USB llega como "Generic X-Box pad" (lo renombra
    // el driver xpad) y el vendor id es lo único que queda para reconocerlo.
    // Ojo que el MISMO mando en modo PS4 por Bluetooth se declara 0x054C, o sea
    // Sony, y ahí no hay forma de distinguirlo de un DualShock de verdad por
    // software: para eso está el override manual, y por eso la UI muestra
    // siempre qué resolvió el automático.
    private const int GameSirVendorId = 0x3537;
    private const int SonyVendorId = 0x054C;

    // El campo del config es un STRING y no este enum a propósito.
    // LoadModConfig es un JsonConvert.DeserializeObject pelado, y un enum con
    // un valor que no conocemos tira dentro de StartClientSide y se lleva el
    // mod entero (mismo motivo que LoadConfigSafe). Con string + parseo
    // tolerante, un valor raro escrito a mano cae en Auto y nadie se entera.
    public static GlyphStyle Parse(string? value) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        "off" or "none" or "no" or "false" or "0"       => GlyphStyle.Off,
        "xbox" or "xb" or "x360" or "xinput"            => GlyphStyle.Xbox,
        "playstation" or "ps" or "sony"
            or "dualshock" or "dualsense"
            or "ps3" or "ps4" or "ps5"                  => GlyphStyle.PlayStation,
        "nintendo" or "switch" or "pro" or "joycon"     => GlyphStyle.Nintendo,
        _                                               => GlyphStyle.Auto,
    };

    // Lo que se persiste en el JSON. En minúscula y sin acentos: es un valor de
    // config, no texto de UI.
    public static string Serialize(GlyphStyle style) => style switch
    {
        GlyphStyle.Off         => "off",
        GlyphStyle.Xbox        => "xbox",
        GlyphStyle.PlayStation => "playstation",
        GlyphStyle.Nintendo    => "nintendo",
        _                      => "auto",
    };

    // Resuelve `Auto` mirando el NOMBRE del device, con el vendor id del GUID
    // como desempate. Nunca el AxisLayout del provider: ese es privado, describe
    // el PROTOCOLO del mando y no su plástico — el GameSir Cyclone 2 se detecta
    // como SonyDs por protocolo y tiene serigrafía A/B/X/Y.
    public static GlyphStyle FromDevice(string? deviceName, int vendorId)
    {
        string n = (deviceName ?? "").ToLowerInvariant();

        // GameSir / Chicken Run hablan protocolo Sony pero el plástico dice
        // A/B/X/Y. Por USB con el driver xpad de Linux el nombre se pierde del
        // todo — un Cyclone 2 reporta "Generic X-Box pad" — así que además del
        // nombre miramos el vendor id, que ahí sí sobrevive.
        if (n.Contains("gamesir") || n.Contains("chicken run")
            || vendorId == GameSirVendorId) return GlyphStyle.Xbox;

        if (n.Contains("nintendo") || n.Contains("switch") || n.Contains("joy-con")
            || n.Contains("joycon") || n.Contains("pro controller")) return GlyphStyle.Nintendo;

        if (n.Contains("dualsense") || n.Contains("dualshock") || n.Contains("playstation")
            || n.Contains("sony") || n.Contains("ps3") || n.Contains("ps4")
            || n.Contains("ps5")) return GlyphStyle.PlayStation;

        // "Wireless Controller" es el nombre que reporta por Bluetooth un
        // DualShock 4 de verdad, y no dice nada de la marca. Desempata el
        // vendor id (0x054C = Sony). Honestidad: un Cyclone 2 en modo PS4
        // también finge 0x054C, y por software es indistinguible — para eso
        // están el override manual y que la UI muestre qué resolvió el auto.
        if (n.Contains("wireless controller") && vendorId == 0x054C) return GlyphStyle.PlayStation;

        // Steam Deck y Steam Controller usan serigrafía ABXY, así que caen acá.
        // No hace falta una cuarta familia.
        return GlyphStyle.Xbox;
    }

    // Por qué `auto` resolvió lo que resolvió. Va en `.gpglyphs` y en la UI: la
    // mitad de los reportes posibles sobre glifos son "me muestra los botones
    // equivocados", y con esta línea se contestan sin una segunda corrida.
    public static string ExplainAuto(string? deviceName, int vendorId)
    {
        string n = (deviceName ?? "").ToLowerInvariant();
        if (n.Contains("gamesir") || n.Contains("chicken run"))
            return "por nombre (GameSir habla protocolo Sony pero tiene serigrafía ABXY)";
        if (vendorId == GameSirVendorId)
            return "por vendor id 0x3537 (GameSir; el driver xpad le borra el nombre)";
        if (n.Contains("nintendo") || n.Contains("switch") || n.Contains("joy-con")
            || n.Contains("joycon") || n.Contains("pro controller")) return "por nombre";
        if (n.Contains("dualsense") || n.Contains("dualshock") || n.Contains("playstation")
            || n.Contains("sony") || n.Contains("ps3") || n.Contains("ps4")
            || n.Contains("ps5")) return "por nombre";
        if (n.Contains("wireless controller") && vendorId == SonyVendorId)
            return "por vendor id 0x054C (nombre genérico de un DualShock 4 por BT)";
        return string.IsNullOrEmpty(n) ? "sin mando: default" : "default, no reconocí ni el nombre ni el vendor id";
    }
}
