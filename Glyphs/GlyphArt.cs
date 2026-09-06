namespace GamepadCompanion.Glyphs;

using System;
using System.Collections.Generic;
using Cairo;
using Vintagestory.API.Client;

// Los cuatro símbolos de las caras de PlayStation, dibujados como PATHS DE CAIRO.
//
// La primera versión usaba los PNG de Kenney (CC0) y se descartó después de
// mirarlos en los dos lugares donde aparecen:
//
//   · En el cartel la cápsula mide ~23 px y el bitmap se veía bien.
//   · En una página del manual mide 14 px, y ahí cualquier bitmap es un borrón —
//     mientras que la palabra "Square" se lee perfecto a ese tamaño.
//   · Y el tinte no tiene una respuesta única: el cartel flota sobre el mundo
//     (claro) y el manual sobre pergamino (oscuro), así que un color fijo se
//     pierde en uno de los dos.
//
// Un path no tiene ninguno de esos problemas: es nítido a cualquier tamaño y se
// le puede dar EL MISMO tratamiento que a las letras de al lado — contorno marrón
// oscuro y relleno del color del texto — que es justamente lo que hace que las
// letras se lean sobre cualquier fondo. De paso el mod no necesita empaquetar
// ninguna textura ni depender de cómo el AssetManager decodifica un PNG con
// paleta.
internal static class GlyphArt
{
    private static readonly HashSet<string> Faces =
        new(StringComparer.Ordinal) { "Cross", "Circle", "Square", "Triangle" };

    // Barato y sin estado: lo llama el prefix en cada cápsula que dibuja.
    public static bool IsFaceLabel(string? label) => label is not null && Faces.Contains(label);

    // La cápsula entera con el símbolo adentro. Replica DrawHotkey de vanilla con
    // el mismo orden y las mismas cuentas — incluido el cast a int del avance —
    // cambiando sólo el texto por el símbolo. Devuelve el avance.
    //
    // Vive acá y no dentro del prefix para que el harness offline lo dibuje
    // EXACTAMENTE igual y no con una copia.
    public static double DrawCapsule(ICoreClientAPI capi, Context ctx, string label,
                                     double x, double y, CairoFont font, double lineheight,
                                     double textHeight, double plusWidth, double symbolSpacing,
                                     double leftRightPadding, double[] color)
    {
        if (x > 0.0)
        {
            capi.Gui.Text.DrawTextLine(ctx, font, "+", x + symbolSpacing,
                y + (lineheight - textHeight) / 2.0 + GuiElement.scaled(2.0));
            x += plusWidth + 2.0 * symbolSpacing;
        }

        double width = font.GetTextExtents(label).Width;
        double reserved = (int)(width + GuiElement.scaled(leftRightPadding * 2.0));
        // La placa es cuadrada aunque el texto que la midió sea "Triangle": un
        // símbolo cuadrado centrado en una caja de 76 px se ve como un punto
        // perdido adentro de una caja vacía.
        double boxWidth = Math.Min(reserved, lineheight);

        // El avance depende de DÓNDE estamos, y no es un capricho:
        //
        //  · En el CARTEL el ancho se acumula mientras se dibuja (drawHelp suma los
        //    retornos y recién al final escribe ActualWidth), así que se puede
        //    devolver el ancho real de la placa: la línea queda más compacta y con
        //    más aire contra el techo de 600, justo donde "Triangle" era el peor
        //    caso. Ahí la placa va pegada a la izquierda, o se saldría del avance.
        //  · En PROSA no se puede: el ancho ya lo reservó DisplayText midiendo el
        //    texto, antes de dibujar. Se devuelve lo mismo que vanilla y la placa
        //    se centra en ese espacio.
        bool tight = GlyphScope.InSign;
        double plateX = tight ? x + 1.0 : x + 1.0 + (reserved - boxWidth) / 2.0;

        GuiElement.RoundRectangle(ctx, plateX, y + 1.0, boxWidth, lineheight, 3.5);
        ctx.SetSourceRGBA(color);
        ctx.LineWidth = 1.5;
        ctx.StrokePreserve();
        ctx.SetSourceRGBA(color[0], color[1], color[2], color[3] * 0.5);
        ctx.Fill();

        DrawFace(ctx, label, plateX + boxWidth / 2.0, y + 1.0 + lineheight / 2.0,
                 lineheight * 0.30, color);

        return (int)(x + symbolSpacing + (tight ? boxWidth : reserved));
    }

    // El símbolo, centrado en (cx, cy) con radio r. Mismo tratamiento que el texto
    // de al lado: se traza el contorno grueso en marrón oscuro y encima el trazo
    // fino del color del texto. Es lo que hace legibles a las letras sobre
    // cualquier fondo, y por eso funciona igual sobre el mundo y sobre pergamino.
    public static void DrawFace(Context ctx, string label, double cx, double cy,
                                double r, double[] color)
    {
        ctx.Save();
        try
        {
            Path(ctx, label, cx, cy, r);
            ctx.LineCap = LineCap.Round;
            ctx.LineJoin = LineJoin.Round;
            ctx.LineWidth = Math.Max(2.4, r * 0.62);
            ctx.SetSourceRGBA(GuiStyle.DarkBrownColor);
            ctx.StrokePreserve();
            ctx.LineWidth = Math.Max(1.0, r * 0.30);
            ctx.SetSourceRGBA(color);
            ctx.Stroke();
        }
        finally { ctx.Restore(); }
    }

    private static void Path(Context ctx, string label, double cx, double cy, double r)
    {
        ctx.NewPath();
        switch (label)
        {
            case "Cross":
                double d = r * 0.78;              // la ✕ se dibuja dentro del círculo
                ctx.MoveTo(cx - d, cy - d); ctx.LineTo(cx + d, cy + d);
                ctx.MoveTo(cx - d, cy + d); ctx.LineTo(cx + d, cy - d);
                break;
            case "Circle":
                ctx.Arc(cx, cy, r * 0.86, 0.0, Math.PI * 2.0);
                break;
            case "Square":
                double s = r * 0.76;
                ctx.Rectangle(cx - s, cy - s, s * 2.0, s * 2.0);
                ctx.ClosePath();
                break;
            case "Triangle":
                // Centroide en cy, para que no se vea caído dentro de la cápsula.
                double t = r * 0.98;
                ctx.MoveTo(cx, cy - t);
                ctx.LineTo(cx + t * 0.92, cy + t * 0.72);
                ctx.LineTo(cx - t * 0.92, cy + t * 0.72);
                ctx.ClosePath();
                break;
        }
    }
}
