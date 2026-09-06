namespace GamepadCompanion.Glyphs;

using System;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

// Reemplaza los íconos de mouse del cartel flotante por una cápsula con la
// etiqueta del gatillo. SIN HARMONY: `IconUtil.CustomIcons` es un diccionario
// público que `DrawIconInt` consulta ANTES de su propio switch, así que alcanza
// con poner una entrada.
//
// La cápsula es CUADRADA, y no es una decisión estética. El avance que el
// llamador reserva para el ícono es fijo — `x + lineheight + symbolspacing + 1`,
// sin medir lo que se dibujó — así que un glifo más ancho que la caja no se
// recorta: PISA el ": Abrir" que se dibuja después. Cuadrado, el costo de ancho
// es exactamente cero en cualquier idioma y a cualquier GUIScale, y eso importa
// porque cada línea del cartel tiene un techo duro de 600 unidades y el peor
// caso en alemán ya está a 9 px de ese techo con el ícono de vanilla.
internal sealed class MouseIconOverride
{
    // Alto de fuente como fracción del alto de la caja.
    //
    // El punto de partida fue 20/30 = 0,667, que es la relación exacta del cartel
    // del bloque mirado (fuente 20 en una caja de 30): con eso la etiqueta sale
    // del MISMO tamaño que la cápsula de tecla de al lado y la línea no se lee
    // como un parche. Pero a esa relación las letras llegan hasta el borde de la
    // placa, porque nuestra caja es FIJA mientras que la cápsula de vanilla crece
    // con su texto y se queda con ~4 px de aire adentro. A GUIScale 0,5 eso se ve
    // como un borrón. 0,58 le devuelve el aire sin cambiar ninguna decisión de
    // dibujar-o-rendirse en ninguna de las tres cajas (verificado con
    // `gpclab boxes`, y el antes/después con `gpclab render --scale=0.5 --zoom=6`).
    //
    // Derivarlo de la caja RECIBIDA y no de una constante es lo que hace que
    // funcione en las otras dos: el mismo delegate lo llaman el hint del ítem en
    // mano (caja 25, fuente 16) y el tag <icon> del tutorial (caja = el alto de
    // la fuente de la prosa, 18 ó 15). Medido con `gpclab boxes`: con una fuente
    // fija de 20, en la caja del tag <icon> la etiqueta se pasa a TODOS los
    // GUIScale — esa caja es un cuadrado del alto de la fuente, y dos letras a
    // esa fuente siempre son más anchas que altas.
    private const double FontToBox = 0.58;

    // Debajo de esto no se dibuja: se deja el ícono de vanilla. Un mouse
    // reconocible es mejor que un borrón de 5 px. Pasa en el tag <icon> y en el
    // hint del ítem en mano a GUIScale 0,5, donde las cajas miden 9 y 12,5 px.
    private const double MinLegiblePx = 8.0;

    private const double PadUnscaled = 1.0;

    private readonly ICoreClientAPI capi;

    [ThreadStatic] private static bool reentering;

    public MouseIconOverride(ICoreClientAPI capi) => this.capi = capi;

    // `capi.Gui.Icons` es POR SESIÓN DE MUNDO (GuiAPI construye su IconUtil en el
    // ctor), así que esto se registra en cada StartClientSide y no hace falta
    // limpiarlo en Dispose. Es el mismo patrón que usa vanilla en HudHotbar.
    //
    // Se registra SIEMPRE, con o sin mando: la decisión se toma adentro del
    // delegate leyendo estado vivo. Así, enchufar el mando no re-registra nada,
    // sólo dispara una recomposición.
    public void Install()
    {
        var icons = capi.Gui.Icons;
        icons.CustomIcons["leftmousebutton"]  = (ctx, x, y, w, h, rgba) => Draw(ctx, x, y, w, h, rgba, left: true);
        icons.CustomIcons["rightmousebutton"] = (ctx, x, y, w, h, rgba) => Draw(ctx, x, y, w, h, rgba, left: false);
    }

    private void Draw(Context ctx, int x, int y, float w, float h, double[] rgba, bool left)
    {
        if (reentering) { Vanilla(ctx, x, y, w, h, rgba, left); return; }
        reentering = true;

        // Este delegate corre sobre el Context de un ImageSurface AJENO, donde el
        // llamador ya llamó SetupContext con SU fuente. Si lo devolvemos con otra
        // fuente o con otro color, lo que se rompe es el texto que se dibuja
        // DESPUÉS en la misma línea. Cairo guarda font face, tamaño y source en
        // el estado gráfico, así que Save/Restore alcanza — vanilla resuelve lo
        // mismo a mano con un SetSourceRGBA al salir.
        ctx.Save();
        try
        {
            // La sesión se lee del estático y no de un campo capturado a
            // propósito: al salir del mundo queda en null, y este delegate sigue
            // siendo alcanzable. Con una sesión muerta preguntaríamos por
            // bindings a una API destruida.
            GlyphSession? session = GlyphRuntime.Session;
            // Todo-o-nada por línea: dentro de una línea del cartel que no se
            // puede convertir entera, el ícono de mouse también se queda en
            // vanilla. Sin esto, una línea con un modificador que no sabemos
            // traducir saldría mitad glifo y mitad teclado. Fuera del cartel
            // (el tag <icon> del tutorial) InSign es false y se convierte normal.
            bool blockedByLine = GlyphScope.InSign && !GlyphScope.SignConvertible;
            string? label = session is not null && session.Icons.Usable && !blockedByLine
                ? session.Resolver.LabelForMouse(left ? 0 : 2)
                : null;

            if (label is null || !TryDrawSquareCapsule(capi, ctx, label, x, y, w, h, rgba))
                Vanilla(ctx, x, y, w, h, rgba, left);
        }
        catch (Exception e)
        {
            // Si esto tira, lo que se rompe es la composición de un HUD del
            // juego, no una GUI nuestra.
            GlyphRuntime.Session?.Icons.ReportFailure(e);
            try { Vanilla(ctx, x, y, w, h, rgba, left); } catch { }
        }
        finally
        {
            ctx.Restore();
            reentering = false;
        }
    }

    // DrawLeftMouseButton y DrawRightMouseButton son públicos y NO consultan
    // CustomIcons. Llamar a DrawIcon con el mismo nombre desde acá sería
    // recursión infinita: DrawIconInt mira el diccionario antes que su switch.
    private void Vanilla(Context ctx, int x, int y, float w, float h, double[] rgba, bool left)
    {
        if (left) capi.Gui.Icons.DrawLeftMouseButton(ctx, x, y, w, h, rgba);
        else      capi.Gui.Icons.DrawRightMouseButton(ctx, x, y, w, h, rgba);
    }

    // Devuelve false si la caja es demasiado chica para que la etiqueta se lea;
    // ahí el llamador dibuja el ícono de vanilla. Es estático y recibe el capi
    // para que `gpclab render` dibuje EXACTAMENTE esto y no una copia que se
    // desincroniza.
    internal static bool TryDrawSquareCapsule(ICoreClientAPI capi, Context ctx, string label,
                                              double x, double y, double w, double h,
                                              double[] rgba)
    {
        if (!Fit(label, w, h, rgba, out CairoFont font, out double textW, out _)) return false;

        // Placa: mismo dibujo que la cápsula de tecla de vanilla — borde de 1.5
        // al color del hint y relleno al 50 % de alpha.
        GuiElement.RoundRectangle(ctx, x + 1.0, y + 1.0, w - 2.0, h - 2.0, 3.5);
        ctx.SetSourceRGBA(rgba);
        ctx.LineWidth = 1.5;
        ctx.StrokePreserve();
        ctx.SetSourceRGBA(rgba[0], rgba[1], rgba[2], rgba[3] * 0.5);
        ctx.Fill();

        // DrawTextLine dibuja con la fuente que tenga puesta el CONTEXT, no con
        // la del CairoFont que recibe: sin este SetupContext sale la fuente por
        // defecto de Cairo, de 10 px.
        font.SetupContext(ctx);
        double textH = font.GetFontExtents().Height;
        capi.Gui.Text.DrawTextLine(ctx, font, label,
                                   x + (w - textW) / 2.0,
                                   y + (h - textH) / 2.0);
        return true;
    }

    // La parte que DECIDE, separada de la que dibuja: así `gpclab boxes` reporta
    // exactamente lo que va a pasar en el juego en vez de rehacer la cuenta por
    // su cuenta y desincronizarse. Devuelve false cuando hay que rendirse y
    // dejar el ícono de vanilla.
    internal static bool Fit(string label, double w, double h, double[] rgba,
                             out CairoFont font, out double textW, out double drawnPx)
    {
        float scale = RuntimeEnv.GUIScale <= 0f ? 1f : RuntimeEnv.GUIScale;
        double innerW = Math.Max(1.0, w - 2.0 * GuiElement.scaled(PadUnscaled));

        // UnscaledFontsize se vuelve a multiplicar por GUIScale en SetupContext,
        // así que para dibujar a N píxeles hay que pedir N/scale.
        double unscaled = h * FontToBox / scale;
        font = CairoFont.WhiteMediumText()
                        .WithColor(rgba)
                        // El texto del cartel lleva contorno marrón de 2 px: es lo
                        // que lo hace legible tanto sobre cielo claro como dentro
                        // de una cueva. Un glifo pelado no hereda nada de eso.
                        .WithStroke(GuiStyle.DarkBrownColor, 2.0)
                        .WithFontSize((float)unscaled);

        textW = font.GetTextExtents(label).Width;
        if (textW > innerW && textW > 0.0)
        {
            unscaled *= innerW / textW;
            font.WithFontSize((float)unscaled);
            textW = font.GetTextExtents(label).Width;
        }

        drawnPx = unscaled * scale;
        return drawnPx >= MinLegiblePx;
    }
}
