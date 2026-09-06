namespace GamepadCompanion.Glyphs;

using System;
using System.Collections.Generic;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

// Los cuatro símbolos de las caras de PlayStation, en PNG.
//
// PNG y no SVG a propósito: la rama sin tinte de SvgLoader NO premultiplica el
// alfa, y los composers del juego trabajan con alfa premultiplicado, así que un
// SVG a color sale con halo. Un PNG cargado a ImageSurface y compuesto con Cairo
// es compositing de verdad.
//
// El símbolo se dibuja DENTRO de la caja que el string ya midió: la cápsula la
// sigue dimensionando el texto ("Cross"), así que el avance de retorno es
// idéntico al de vanilla y ninguna medición río arriba se entera. Es la única
// forma de meter arte sin romper el ancho reservado — ni el mod competidor lo
// resolvió, y por eso dibuja un cuadrado.
internal sealed class GlyphArt : IDisposable
{
    // Tamaño nativo de los PNG de Kenney.
    private const int SourceSize = 64;

    private static readonly Dictionary<string, string> ByLabel = new(StringComparer.Ordinal)
    {
        ["Cross"]    = "ps_cross.png",
        ["Circle"]   = "ps_circle.png",
        ["Square"]   = "ps_square.png",
        ["Triangle"] = "ps_triangle.png",
    };

    private readonly ICoreClientAPI capi;
    private readonly GlyphChannel channel;
    private readonly Dictionary<string, ImageSurface?> cache = new(StringComparer.Ordinal);

    public GlyphArt(ICoreClientAPI capi, GlyphChannel channel)
    {
        this.capi = capi;
        this.channel = channel;
    }

    // Barato y sin tocar disco: lo llama el prefix en cada cápsula que dibuja.
    public static bool IsFaceLabel(string? label)
        => label is not null && ByLabel.ContainsKey(label);

    // Carga perezosa: si nadie usa el set de PlayStation, el arte no se lee nunca.
    // Un fallo se cachea como null para no reintentar por cápsula.
    public ImageSurface? Get(string label)
    {
        if (cache.TryGetValue(label, out ImageSurface? cached)) return cached;
        if (!ByLabel.TryGetValue(label, out string? file)) return null;

        ImageSurface? surface = null;
        try
        {
            IAsset? asset = capi.Assets.TryGet(
                new AssetLocation("gamepadcompanion", "textures/glyphs/" + file));
            if (asset is null)
                channel.MarkUnavailable("falta la textura textures/glyphs/" + file);
            else if (asset.ToBitmap(capi) is BitmapExternal bitmap)
            {
                using (bitmap)
                    surface = GuiElement.getImageSurfaceFromAsset(bitmap, SourceSize, SourceSize);
            }
        }
        catch (Exception e)
        {
            channel.ReportFailure(e);
        }

        cache[label] = surface;
        return surface;
    }

    // La cápsula entera, con el símbolo adentro. Replica DrawHotkey de vanilla con
    // el mismo orden y las mismas cuentas — incluido el cast a int del avance —
    // cambiando sólo el texto por el símbolo. Devuelve el avance.
    //
    // Vive acá y no dentro del prefix para que el harness offline
    // (`gpclab render`) dibuje EXACTAMENTE esto y no una copia.
    public static double DrawCapsule(ICoreClientAPI capi, Context ctx, string label,
                                     ImageSurface symbol, double x, double y, CairoFont font,
                                     double lineheight, double textHeight, double plusWidth,
                                     double symbolSpacing, double leftRightPadding, double[] color)
    {
        if (x > 0.0)
        {
            capi.Gui.Text.DrawTextLine(ctx, font, "+", x + symbolSpacing,
                y + (lineheight - textHeight) / 2.0 + GuiElement.scaled(2.0));
            x += plusWidth + 2.0 * symbolSpacing;
        }

        double width = font.GetTextExtents(label).Width;
        double reserved = (int)(width + GuiElement.scaled(leftRightPadding * 2.0));
        // La PLACA es cuadrada aunque el texto que la midió sea "Triangle": un
        // símbolo cuadrado centrado en una caja de 76 px se ve como un punto
        // perdido adentro de una caja vacía.
        double boxWidth = Math.Min(reserved, lineheight);

        // Y el avance depende de DÓNDE estamos, que no es un capricho:
        //
        //  · En el CARTEL el ancho se acumula mientras se dibuja (drawHelp suma los
        //    retornos y recién al final escribe ActualWidth), así que se puede
        //    devolver el ancho real de la placa. La línea queda más compacta y con
        //    más aire contra el techo de 600, justo donde "Triangle" era el peor
        //    caso. Ahí la placa va pegada a la izquierda, o se saldría del avance.
        //  · En PROSA no se puede: el ancho ya lo reservó DisplayText midiendo el
        //    texto, antes de dibujar. Se devuelve lo mismo que vanilla y la placa
        //    se centra en ese espacio; sobra aire, que es mucho menos feo que
        //    desalinear la textura.
        bool tight = GlyphScope.InSign;
        double plateX = tight ? x + 1.0 : x + 1.0 + (reserved - boxWidth) / 2.0;
        GuiElement.RoundRectangle(ctx, plateX, y + 1.0, boxWidth, lineheight, 3.5);
        ctx.SetSourceRGBA(color);
        ctx.LineWidth = 1.5;
        ctx.StrokePreserve();
        ctx.SetSourceRGBA(color[0], color[1], color[2], color[3] * 0.5);
        ctx.Fill();

        // Cuadrado y centrado en la caja. 0,72 del alto de línea deja el símbolo
        // del porte de una mayúscula de la fuente de al lado.
        double size = lineheight * 0.78;
        Paint(ctx, symbol,
              plateX + (boxWidth - size) / 2.0,
              y + 1.0 + (lineheight - size) / 2.0,
              size);

        return (int)(x + symbolSpacing + (tight ? boxWidth : reserved));
    }

    // Pinta el símbolo cuadrado, escalado, en (x, y). El Save/Restore es del
    // llamador: acá se toca la matriz del contexto.
    public static void Paint(Context ctx, ImageSurface surface, double x, double y, double size)
    {
        ctx.Save();
        ctx.Translate(x, y);
        ctx.Scale(size / SourceSize, size / SourceSize);
        var pattern = new SurfacePattern(surface) { Filter = Filter.Good };
        ctx.SetSource(pattern);
        ctx.Paint();
        pattern.Dispose();
        ctx.Restore();
    }

    public void Dispose()
    {
        foreach (ImageSurface? surface in cache.Values) surface?.Dispose();
        cache.Clear();
    }
}
