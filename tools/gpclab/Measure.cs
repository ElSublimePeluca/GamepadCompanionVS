using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Cairo;
// Cairo tambien define un tipo Path; en este archivo Path siempre es el de System.IO.
using Path = System.IO.Path;
using GamepadCompanion.Glyphs;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace GamepadCompanion.Lab;

// Un ICoreClientAPI de mentira, armado con DispatchProxy. Sólo contesta lo que los métodos
// de dibujo del engine piden de verdad (capi.Gui.Text y capi.Gui.Icons), y tira una
// excepción con nombre si alguna vez se le pide otra cosa.
//
// Vale la pena la maniobra: con esto el harness llama a HotkeyComponent.DrawHotkey y a
// IconUtil.DrawIcon REALES en vez de a una reimplementación. Un port a mano de esos métodos
// se desincroniza en silencio con el próximo update del juego, que es exactamente el error
// que este banco de pruebas existe para no cometer.
public class Stub : System.Reflection.DispatchProxy
{
    public Func<MethodInfo, object?[]?, object?>? Handler;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (Handler is null || targetMethod is null)
            throw new NotSupportedException("gpclab: proxy sin handler");
        return Handler(targetMethod, args);
    }
}

internal static class Measure
{
    // Las TRES cajas que llaman al mismo delegate de ícono, con la tipografía de cada una.
    // El plan original dimensionó el glifo para la primera solamente; las otras dos son más
    // chicas y son las que desbordan.
    //   nombre                       caja sin escalar   fuente de la línea   evidencia
    private static readonly (string Name, double Box, float Font)[] Sites =
    {
        ("cartel del bloque mirado",       30.0, 20f),  // DrawWorldInteractionUtil (defaults)
        ("hint del ítem en mano",          25.0, 16f),  // HudHotbar.cs:70-72
        ("tag <icon>, tutorial activo",    18.0, 18f),  // IconComponent: caja = scaled(uf)
        ("tag <icon>, tutorial completo",  15.0, 15f),  // ModSystemTutorial.cs:323
    };

    private static readonly float[] Scales = { 0.5f, 1.0f, 1.5f };

    // Lo que el resolver puede llegar a meter en una caja de ícono de MOUSE es
    // exactamente un gatillo, en cualquiera de las tres familias — el ícono de
    // mouse sólo se dibuja para primarymouse/secondarymouse, y esos los hacen los
    // gatillos. Se sacan de la tabla real del mod, no de una lista a mano, para
    // que agregar una familia no deje el harness midiendo lo de ayer.
    // Las últimas cuatro son las peores etiquetas del set completo: no caen nunca
    // en una caja de ícono, pero sirven de referencia de cuánto pide una cápsula.
    private static readonly string[] Candidates =
        new[] { GamepadInput.TriggerLeft, GamepadInput.TriggerRight }
            .SelectMany(_ => new[] { GlyphStyle.Xbox, GlyphStyle.PlayStation, GlyphStyle.Nintendo },
                        (input, style) => GlyphLabels.Label(input, style))
            .Concat(new[] { "RS" + GlyphLabels.ToggleMark, "D-Down", "Menu", "Triangle" })
            .Distinct()
            .ToArray();

    private const double SignCeiling = 600.0;    // ElementBounds.Fixed(0, y, 600, 80)
    private static readonly double[] White = { 1.0, 1.0, 1.0, 1.0 };

    // Lo que separa dos líneas del cartel: ElementBounds.Fixed(0, i*(30+8), ...)
    // en DrawWorldInteractionUtil, o sea 38 unidades sin escalar. Antes acá había
    // un número inventado y por eso el harness nunca mostró el amontonamiento
    // vertical a GUIScale chico.
    private static double SignLineStep() => GuiElement.scaled(30.0 + 8.0);

    public static int Boxes(string gameRoot)
    {
        PrintHeader(gameRoot);
        Console.WriteLine(
            "El avance que el llamador reserva para el ícono es FIJO (x + lineheight +\n" +
            "symbolspacing + 1): un glifo más ancho que la caja no se recorta, PISA el texto\n" +
            "que viene después. Por eso la cápsula es cuadrada y por eso importa que la\n" +
            "etiqueta entre a la fuente de la línea, sin achicarla hasta que no se lea.\n");

        foreach (var (name, unscaledBox, fontSize) in Sites)
        {
            Console.WriteLine($"── {name}  (caja {unscaledBox}, fuente {fontSize})");
            foreach (float scale in Scales)
            {
                RuntimeEnv.GUIScale = scale;
                double box = GuiElement.scaled(unscaledBox);
                double inner = Math.Max(1.0, box - 2.0 * GuiElement.scaled(1.0));
                CairoFont font = LineFont(fontSize);

                Console.WriteLine($"   GUIScale {scale:0.0}  caja={box:0.0}px  interior={inner:0.0}px  " +
                                  $"alto de texto={font.GetFontExtents().Height:0.0}px");

                foreach (string label in Candidates)
                {
                    // La decisión la toma el painter del mod, no una cuenta paralela.
                    bool draws = MouseIconOverride.Fit(label, box, box, White,
                                                       out _, out double textW, out double drawnPx);
                    // Lo que ocuparía la misma etiqueta como cápsula de TEXTO de vanilla,
                    // para ver cuánto ahorra el cuadrado (leftRightPadding real = 10).
                    double capsule = font.GetTextExtents(label).Width + GuiElement.scaled(10.0 * 2.0);
                    Console.WriteLine(
                        $"      {label,-9} {(draws ? "dibuja " : "VANILLA")}" +
                        $" texto={textW,5:0.0}px en {inner,5:0.0}px  fuente={drawnPx,4:0.0}px" +
                        $"   [como cápsula de texto: {capsule:0.0}px de ancho]");
                }
            }
            Console.WriteLine();
        }

        RuntimeEnv.GUIScale = 1.0f;
        Console.WriteLine($"Techo del cartel: {SignCeiling} unidades por línea " +
                          $"(= {SignCeiling * Scales[0]:0}px a GUIScale 0.5, " +
                          $"{SignCeiling * Scales[^1]:0}px a 1.5). Se recorta en silencio, y como\n" +
                          "ActualWidth centra el cartel, el síntoma es \"cortado a la derecha Y corrido\".");
        return 0;
    }

    // La tabla (control, familia) → etiqueta, con el ancho en píxeles de cada una
    // a la tipografía del cartel. Sirve para elegir una etiqueta nueva sin
    // adivinar: si mide más que el interior de la caja de ícono, no entra.
    public static int Labels(string gameRoot)
    {
        PrintHeader(gameRoot);
        RuntimeEnv.GUIScale = 1.0f;
        CairoFont font = LineFont(20f);

        var styles = new[] { GlyphStyle.Xbox, GlyphStyle.PlayStation, GlyphStyle.Nintendo };
        Console.WriteLine($"  {"control",-18}" + string.Concat(styles.Select(st => $"{st,-24}")));
        Console.WriteLine($"  {new string('─', 18 + 24 * styles.Length)}");

        foreach (GamepadInput input in Enum.GetValues<GamepadInput>())
        {
            Console.Write($"  {input,-18}");
            foreach (GlyphStyle style in styles)
            {
                string label = GlyphLabels.Label(input, style);
                Console.Write($"{label + $" ({font.GetTextExtents(label).Width:0}px)",-24}");
            }
            Console.WriteLine();
        }

        DialogStrings();

        Console.WriteLine(
            $"\n  Anchos a fuente 20 / GUIScale 1, la del cartel del bloque mirado.\n" +
            $"  La marca \"{GlyphLabels.ToggleMark}\" que llevan L3/R3 suma " +
            $"{font.GetTextExtents(GlyphLabels.ToggleMark).Width:0}px.\n" +
            "  Para saber si una etiqueta entra en cada caja de ícono: gpclab boxes.");
        return 0;
    }

    // Los textos que el mod dibuja en su propio diálogo, medidos contra la caja
    // que les toca, EN LOS TRES IDIOMAS. Los botones de VS se agrandan solos para
    // que el label entre en una línea y nadie los clippea, así que un label largo
    // se dibuja fuera del panel, flotando sobre el mundo — ya pasó y salió el
    // issue #7. Y las tabs no reparten el ancho: si la suma se pasa del interior,
    // la barra scrollea con flechitas.
    //
    // Se leen de los JSON de verdad, no de una copia, para que esto no mida lo
    // de ayer.
    private static void DialogStrings()
    {
        string? langDir = FindRepoFile("assets/gamepadcompanion/lang");
        if (langDir is null) { Console.WriteLine("\n  (no encontré los JSON de idioma)"); return; }

        const double dialogInner = 480 - 2 * 16;   // ConfigDialog: DialogW - 2*Margin
        const double hintLabelW = 210;             // ConfigDialog.HintLabelW
        const double hintControlW = 230;           // ConfigDialog.HintControlW
        const double sensLabelW = 185;             // ConfigDialog.SensLabelW
        var tabKeys = new[] { "tab-wheel", "tab-buttons", "tab-sensitivity", "tab-hints" };
        var buttonKeys = new[] { "hints-style-off", "hints-style-auto", "hints-style-auto-resolved" };
        // Cada texto contra LA CAJA QUE LE TOCA y con la fuente que el código le
        // pone de verdad. Medirlos todos contra el ancho del diálogo es lo que
        // dejó pasar el primer desborde: `AddStaticText` no recorta, envuelve, y
        // la segunda línea se come la fila de abajo.
        var textKeys = new (string Key, bool Detail, double Box)[]
        {
            ("hints-style", false, hintLabelW),
            ("hints-wheel", false, hintLabelW),
            ("hints-preview", false, hintLabelW),
            ("hints-no-gamepad", true, dialogInner),
            ("hints-toggle-note", true, dialogInner),
            // De referencia: las etiquetas de la tab Sensibilidad, que ya existían.
            ("sens-swap-triggers", false, sensLabelW),
            ("sens-invert-pitch", false, sensLabelW),
            ("sens-yaw", false, sensLabelW),
            ("sens-pitch", false, sensLabelW),
            ("sens-deadzone", false, sensLabelW),
        };

        Console.WriteLine("\n  textos del diálogo de config, por idioma");
        foreach (string file in Directory.GetFiles(langDir, "*.json").OrderBy(f => f))
        {
            Dictionary<string, string>? lang;
            try
            {
                lang = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                    File.ReadAllText(file));
            }
            catch (Exception e) { Console.WriteLine($"    {Path.GetFileName(file)}: {e.Message}"); continue; }
            if (lang is null) continue;

            foreach (float scale in new[] { 1.0f, 1.5f })
            {
                RuntimeEnv.GUIScale = scale;
                // La barra de tabs mide cada tab por su texto: si la suma pasa el
                // interior, scrollea. GuiElementHorizontalTabs usa SmallFontSize
                // con 4 de padding por lado y 5 de separación.
                CairoFont tabFont = CairoFont.WhiteSmallText();
                double tabs = tabKeys.Sum(k => Unscaled(tabFont, Text(lang, k)) + 2 * 4 + 5);
                string verdict = tabs <= dialogInner ? "ok" : "SE PASA";

                Console.WriteLine($"    {Path.GetFileNameWithoutExtension(file),-8} GUIScale {scale:0.0}  " +
                                  $"barra de tabs = {tabs:0}/{dialogInner:0}  {verdict}");

                CairoFont btnFont = CairoFont.SmallButtonText();
                foreach (string key in buttonKeys)
                {
                    string text = Text(lang, key).Replace("{0}", "PlayStation");
                    double w = Unscaled(btnFont, text) + 1;   // AutoBoxSize suma 1
                    Console.WriteLine($"        botón  {text,-32} {w,5:0}/{hintControlW:0}  " +
                                      (w <= hintControlW ? "ok" : "se recorta con GuiTextFit"));
                }
                foreach (var (key, detail, box) in textKeys)
                {
                    CairoFont textFont = detail ? CairoFont.WhiteDetailText() : CairoFont.WhiteSmallText();
                    string text = Text(lang, key).Replace("{0}", "*");
                    double w = Unscaled(textFont, text);
                    Console.WriteLine($"        texto  {Trim(text, 32),-32} {w,5:0}/{box,3:0}  " +
                                      (w <= box ? "ok" : "SE ENVUELVE A 2 LÍNEAS"));
                }
            }
        }
        RuntimeEnv.GUIScale = 1.0f;
    }

    private static string Text(Dictionary<string, string> lang, string key) =>
        lang.TryGetValue(key, out string? v) ? v : $"<falta {key}>";

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    // ElementBounds.Fixed come unidades SIN escalar; las extents vienen escaladas.
    private static double Unscaled(CairoFont font, string text)
        => font.GetTextExtents(text).Width / Math.Max(0.01f, RuntimeEnv.GUIScale);

    private static string? FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GamepadCompanion.csproj")))
            dir = dir.Parent;
        if (dir is null) return null;
        string path = Path.Combine(dir.FullName, relative);
        return Directory.Exists(path) || File.Exists(path) ? path : null;
    }

    public static int Render(string gameRoot, string[] args)
    {
        PrintHeader(gameRoot);
        string output = args.FirstOrDefault(a => !a.StartsWith('-'))
                     ?? Path.Combine(Path.GetTempPath(), "gpclab-render.png");

        ICoreClientAPI capi = FakeApi(out IconUtil icons);

        // El ícono de mouse lo dibuja el painter REAL del mod
        // (MouseIconOverride), no una copia: si mañana cambia el redondeo de la
        // placa o el tamaño de fuente, esta vista previa cambia con él.
        void MouseGlyph(Context ctx, string? label, ref double x, double y, CairoFont font, double lh)
        {
            // El "+" de separación lo dibuja el llamador cuando ya hay algo a la
            // izquierda (DrawWorldInteractionUtil.DrawMouseButton), y ocupa
            // ancho. Sin esto el harness dibujaba la línea más angosta que el
            // juego y no mostraba el amontonamiento real.
            double textH = font.GetFontExtents().Height;
            if (x > 0.0)
            {
                capi.Gui.Text.DrawTextLine(ctx, font, "+", (int)x + 5.0,
                                           y + (int)((lh - textH) / 2.0) + 2.0);
                x += font.GetTextExtents("+").Width + 2.0 * 5.0;
            }
            // Save/Restore alrededor: es lo que hace MouseIconOverride.Draw en el
            // juego, y NO es opcional. El painter deja su propia fuente puesta en
            // el Context, y sin restaurarla se filtra al resto de la línea — que
            // es exactamente lo que este harness dibujó mal la primera vez.
            ctx.Save();
            bool drawn = label is not null &&
                MouseIconOverride.TryDrawSquareCapsule(capi, ctx, label, x, y + 1.0, lh, lh, font.Color);
            ctx.Restore();
            if (!drawn) icons.DrawIcon(ctx, "rightmousebutton", x, y + 1.0, lh, lh, font.Color);
            x += lh + 5.0 + 1.0;                       // avance FIJO del llamador
        }

        // La caja del ítem que el cartel dibuja delante cuando la interacción
        // pide un stack — es lo primero de la línea del Carry de CarryOn.
        void Stack(Context ctx, ref double x, double y, CairoFont font, double lh)
        {
            GuiElement.RoundRectangle(ctx, x, y + 1.0, lh, lh, 3.5);
            ctx.SetSourceRGBA(font.Color);
            ctx.LineWidth = 1.5;
            ctx.StrokePreserve();
            ctx.SetSourceRGBA(1.0, 1.0, 1.0, 0.5);
            ctx.Fill();
            ctx.SetSourceRGBA(1.0, 1.0, 1.0, 1.0);
            x += lh + 5.0 + 1.0;
        }

        // Un cartel de verdad: dos líneas, la segunda con modificador, que es el
        // caso real de un cofre con CarryOn instalado. El texto de la cola sale
        // de la misma clave que usa el juego.
        var lines = new (string Label, Action<Context, double, double, CairoFont, double> Draw)[]
        {
            ("vanilla", (ctx, x, y, font, lh) =>
            {
                MouseGlyph(ctx, null, ref x, y, font, lh);
                Tail(capi, ctx, font, x, y, lh, ": Abrir");
                x = 0; y += SignLineStep();
                Stack(ctx, ref x, y, font, lh);
                x = Hotkey(capi, "Shift", x, y, ctx, font, lh);
                MouseGlyph(ctx, null, ref x, y, font, lh);
                Tail(capi, ctx, font, x, y, lh, ": Carry");
            }),
            ("etapa 2: ícono de mouse, tecla todavía de teclado", (ctx, x, y, font, lh) =>
            {
                MouseGlyph(ctx, "LT", ref x, y, font, lh);
                Tail(capi, ctx, font, x, y, lh, ": Abrir");
                x = 0; y += SignLineStep();
                Stack(ctx, ref x, y, font, lh);
                x = Hotkey(capi, "Shift", x, y, ctx, font, lh);
                MouseGlyph(ctx, "LT", ref x, y, font, lh);
                Tail(capi, ctx, font, x, y, lh, ": Carry");
            }),
            ("etapa 3: la línea entera", (ctx, x, y, font, lh) =>
            {
                MouseGlyph(ctx, "LT", ref x, y, font, lh);
                Tail(capi, ctx, font, x, y, lh, ": Abrir");
                x = 0; y += SignLineStep();
                Stack(ctx, ref x, y, font, lh);
                x = Hotkey(capi, "RS*", x, y, ctx, font, lh);
                MouseGlyph(ctx, "LT", ref x, y, font, lh);
                Tail(capi, ctx, font, x, y, lh, ": Carry");
            }),
        };

        // --scale=N dibuja una sola escala, para poder mirarla de cerca.
        float[] scales = args.FirstOrDefault(a => a.StartsWith("--scale=", StringComparison.Ordinal))
                             is string sc && float.TryParse(sc["--scale=".Length..],
                                 System.Globalization.NumberStyles.Float,
                                 System.Globalization.CultureInfo.InvariantCulture, out float only)
            ? new[] { only } : Scales;

        const int width = 720;
        int rowsPerScale = lines.Length;
        RuntimeEnv.GUIScale = Scales[^1];
        int rowH = (int)(2 * SignLineStep() + 16);
        int bandH = rowsPerScale * rowH + 26;
        var surface = new ImageSurface(Format.Argb32, width, bandH * scales.Length * 2);
        var ctx0 = new Context(surface);

        int top = 0;
        foreach (bool dark in new[] { false, true })
        {
            foreach (float scale in scales)
            {
                RuntimeEnv.GUIScale = scale;
                double lh = GuiElement.scaled(30.0);
                CairoFont font = LineFont(20f);

                // Cielo claro y cueva oscura: el contorno marrón de 2px es justamente lo que
                // hace que la etiqueta se lea en los dos, y un glifo sin cápsula no lo hereda.
                ctx0.SetSourceRGBA(dark ? 0.16 : 0.62, dark ? 0.13 : 0.78, dark ? 0.10 : 0.94, 1.0);
                ctx0.Rectangle(0, top, width, bandH);
                ctx0.Fill();

                // Igual que drawHelp:114-116, y NO es opcional: TextDrawUtil.DrawTextLine lee
                // ctx.FontExtents y dibuja con la fuente que tenga puesta el Context, no con
                // la del CairoFont que recibe. Sin esto Cairo usa su fuente por defecto de
                // 10px y el harness dibuja algo que el juego nunca dibujaría.
                font.SetupContext(ctx0);
                double y = top + 8;
                foreach (var (_, draw) in lines)
                {
                    draw(ctx0, 0.0, y, font, lh);
                    y += rowH;
                }
                top += bandH;
            }
        }

        // --zoom N: reescala el resultado con vecino más cercano. A GUIScale 0.5 el
        // cartel mide 15 px de alto y las decisiones de píxeles no se pueden
        // juzgar a tamaño real ni en pantalla ni en una captura.
        int zoom = ZoomFactor(args);
        if (zoom > 1)
        {
            var big = new ImageSurface(Format.Argb32, surface.Width * zoom, surface.Height * zoom);
            var bigCtx = new Context(big);
            bigCtx.Scale(zoom, zoom);
            var pattern = new SurfacePattern(surface) { Filter = Filter.Nearest };
            bigCtx.SetSource(pattern);
            bigCtx.Paint();
            pattern.Dispose();
            big.WriteToPng(output);
            bigCtx.Dispose();
            big.Dispose();
        }
        else surface.WriteToPng(output);
        ctx0.Dispose();
        surface.Dispose();
        Console.WriteLine($"  escrito: {output}");
        Console.WriteLine("  filas por banda: " + string.Join(" · ", lines.Select(l => l.Label)));
        Console.WriteLine("  bandas: GUIScale 0.5 / 1.0 / 1.5 sobre cielo, y las mismas sobre cueva.");
        return 0;
    }

    private static int ZoomFactor(string[] args)
    {
        foreach (string arg in args)
            if (arg.StartsWith("--zoom=", StringComparison.Ordinal)
                && int.TryParse(arg["--zoom=".Length..], out int z))
                return Math.Clamp(z, 1, 8);
        return 1;
    }

    // ── plomería ──────────────────────────────────────────────────────────────────────

    // La misma fuente que arma drawHelp: color aclarado, tamaño de la instancia y contorno
    // marrón de 2px.
    private static CairoFont LineFont(float size)
    {
        double[] color = (double[])GuiStyle.DialogDefaultTextColor.Clone();
        for (int i = 0; i < 3; i++) color[i] = (color[i] + 1.0) / 2.0;
        return CairoFont.WhiteMediumText().WithColor(color).WithFontSize(size)
                        .WithStroke(GuiStyle.DarkBrownColor, 2.0);
    }

    // Cápsula de tecla de vanilla, con el DrawHotkey real del engine.
    private static double Hotkey(ICoreClientAPI capi, string label, double x, double y,
                                 Context ctx, CairoFont font, double lineheight)
        => HotkeyComponent.DrawHotkey(capi, label, x, y, ctx, font, lineheight,
                                      font.GetFontExtents().Height,
                                      font.GetTextExtents("+").Width, 5.0, 10.0, font.Color);

    private static void Tail(ICoreClientAPI capi, Context ctx, CairoFont font,
                             double x, double y, double lineheight, string text)
        => capi.Gui.Text.DrawTextLine(ctx, font, text, x - 4.0,
                                      y + (lineheight - font.GetFontExtents().Height) / 2.0 + 2.0);

    private static ICoreClientAPI FakeApi(out IconUtil icons)
    {
        var text = new TextDrawUtil();
        IconUtil iconUtil = null!;

        var gui = DispatchProxy.Create<IGuiAPI, Stub>();
        ((Stub)(object)gui).Handler = (m, _) => m.Name switch
        {
            "get_Text"  => text,
            "get_Icons" => iconUtil,
            _ => throw new NotSupportedException($"gpclab: el harness no implementa IGuiAPI.{m.Name}"),
        };

        var capi = DispatchProxy.Create<ICoreClientAPI, Stub>();
        ((Stub)(object)capi).Handler = (m, _) => m.Name switch
        {
            "get_Gui" => gui,
            _ => throw new NotSupportedException($"gpclab: el harness no implementa ICoreClientAPI.{m.Name}"),
        };

        // El ctor de IconUtil sólo guarda capi y registra un delegate; no lo llama.
        iconUtil = new IconUtil(capi);
        icons = iconUtil;
        return capi;
    }

    private static void PrintHeader(string gameRoot)
    {
        string conf = Environment.GetEnvironmentVariable("FONTCONFIG_FILE") ?? "(sin definir)";
        RuntimeEnv.GUIScale = 1.0f;
        CairoFont probe = CairoFont.WhiteMediumText().WithFontSize(20f);
        double w = probe.GetTextExtents("Shift Right mouse 0123").Width;
        double h = probe.GetFontExtents().Height;

        Console.WriteLine($"gpclab — juego: {gameRoot}");
        Console.WriteLine($"  cwd            : {Directory.GetCurrentDirectory()}");
        Console.WriteLine($"  FONTCONFIG_FILE: {conf}");
        Console.WriteLine($"  fuente         : \"{GuiStyle.StandardFontName}\"  " +
                          $"huella a 20px/GUIScale 1: ancho={w:0.###} alto={h:0.###}");
        Console.WriteLine("  (si la huella no coincide con la de la medición anterior, los números de\n" +
                          "   píxeles de esta corrida NO son comparables con los del plan.)\n");
    }
}
