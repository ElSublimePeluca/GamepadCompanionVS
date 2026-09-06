namespace GamepadCompanion.Glyphs;

using System.Collections.Generic;
using System.Linq;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

// La salida de `.gpglyphs`. Va al chat y al log, igual que `.gpdevice`.
//
// Para un mod cuyo canal de soporte son issues de GitHub y comentarios de
// ModDB, esto ES el feature: dice versión del juego, versión del mod, device,
// familia resuelta y POR QUÉ, estado de los cuatro canales con el motivo de
// cada caída, y el mapa inverso completo. Un reporte se contesta pegando una
// salida, sin ida y vuelta.
internal static class GlyphDiagnostics
{
    // Un renglón para el chat. El volcado entero va al log, que es lo que ya hacen
    // .gpdumphotkeys y .gpguis: el chat de VS no está pensado para 30 líneas, y
    // devolver el texto largo como StatusMessage lo manda además por Lang.Get.
    public static string Summarize(GlyphSession s)
    {
        GlyphResolver r = s.Resolver;
        int keys = 0;
        foreach (var _ in r.KeyEntries) keys++;
        int mice = 0;
        foreach (var _ in r.MouseEntries) mice++;

        var down = new List<string>();
        foreach (GlyphChannel channel in s.Channels)
            if (!channel.Usable) down.Add(channel.Name);

        string family = r.ConfiguredStyle == GlyphStyle.Auto
            ? $"{r.Style} (auto)" : r.Style.ToString();
        string channels = down.Count == 0
            ? "los 4 canales activos"
            : "canales caídos: " + string.Join(", ", down);

        return $"glifos: {family}, {(r.Active ? "sustituyendo" : "inactivo")}, " +
               $"{keys} teclas + {mice} botones de mouse, {channels}. " +
               "El detalle completo está en client-main.log.";
    }

    public static string Describe(GlyphSession s)
    {
        var sb = new StringBuilder();
        GlyphResolver r = s.Resolver;
        GlyphStyle configured = r.ConfiguredStyle;
        GlyphStyle effective = r.Style;

        sb.Append("GamepadCompanion — glifos (juego ")
          .Append(GameVersion.LongGameVersion).Append(", mod ").Append(s.ModVersion).Append(")\n");

        sb.Append("  setting      : GlyphStyle=").Append(GlyphStyles.Serialize(configured))
          .Append("  GlyphWheel=").Append(s.Config.GlyphWheel ? "sí" : "no")
          .Append("  canales apagados: ")
          .Append(string.IsNullOrWhiteSpace(s.Config.GlyphChannelsOff)
                      ? "(ninguno)" : s.Config.GlyphChannelsOff)
          .Append('\n');

        sb.Append("  device       : ")
          .Append(s.Provider.DeviceName is { } name ? $"\"{name}\"" : "(ninguno)")
          .Append("  vendor=0x").Append(s.Provider.VendorId.ToString("X4"))
          .Append("  conectado=").Append(s.Provider.IsConnected ? "sí" : "no").Append('\n');

        sb.Append("  familia      : ").Append(effective);
        if (configured == GlyphStyle.Auto)
            sb.Append("  ← auto, ")
              .Append(GlyphStyles.ExplainAuto(s.Provider.DeviceName, s.Provider.VendorId));
        sb.Append('\n');

        sb.Append("  sustituyendo : ").Append(r.Active ? "sí" : "no");
        if (!r.Active)
            sb.Append(configured == GlyphStyle.Off
                          ? "  (GlyphStyle=off)"
                          : "  (no hay mando conectado)");
        sb.Append('\n');

        foreach (GlyphChannel channel in s.Channels)
            sb.Append("  canal ").Append(channel.Name.PadRight(8)).Append(": ")
              .Append(channel.Describe()).Append('\n');

        var keys = r.KeyEntries.OrderBy(e => e.Key).ToList();
        var mice = r.MouseEntries.ToList();
        sb.Append("  mapa inverso : ").Append(keys.Count).Append(" teclas, ")
          .Append(mice.Count).Append(" botones de mouse, revisión ").Append(r.Revision).Append('\n');

        foreach (var (button, binding) in mice)
            sb.Append("      mouse ").Append(button)
              .Append(button == 0 ? " (izq)" : button == 2 ? " (der)" : " (medio)")
              .Append(" = ").Append(binding.Label.PadRight(9))
              .Append(binding.Input).Append('/').Append(binding.Source).Append('\n');

        foreach (var entry in keys)
            sb.Append("      ").Append($"{(GlKeys)entry.Key} ({entry.Key})".PadRight(18))
              .Append(" = ").Append(entry.Value.Label.PadRight(9))
              .Append(entry.Value.Input).Append('/').Append(entry.Value.Source).Append('\n');

        IReadOnlyList<string> collisions = r.Collisions;
        sb.Append("  colisiones   : ").Append(collisions.Count == 0 ? "ninguna" : "").Append('\n');
        foreach (string collision in collisions)
            sb.Append("      ").Append(collision).Append('\n');

        if (keys.Any(k => k.Value.Label.EndsWith(GlyphLabels.ToggleMark)))
            sb.Append("  nota         : la marca ").Append(GlyphLabels.ToggleMark)
              .Append(" quiere decir que ese botón es un TOGGLE en este mod ")
              .Append("(apretar y soltar), no una tecla que se mantiene.\n");

        return sb.ToString().TrimEnd('\n');
    }
}
