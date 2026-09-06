using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace GamepadCompanion.Lab;

// Vuelca por reflection la superficie de API del engine de la que dependen los glifos del
// issue #8 y la diffea contra un baseline commiteado.
//
// Para qué: el día que Vintage Story se actualiza, esto contesta en dos segundos si algún
// seam cambió — antes de abrir el IDE, y no tres semanas después por un issue de alguien.
// Toda la detección que se pueda hacer offline es detección que el usuario no sufre.
//
// Lo que NO puede ver (y por eso el mod igual chequea en runtime): que el JIT inlinee un
// método en el call site del engine, que otro mod le ponga un prefix con return false, o
// que el CUERPO de un método cambie sin cambiar la firma.
internal static class ApiCheck
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic
                                   | BindingFlags.Instance | BindingFlags.Static
                                   | BindingFlags.FlattenHierarchy;

    // Tipo → miembros de los que depende la implementación. Cualquier cosa que esté acá es
    // algo que el mod toca; si agregás una dependencia nueva al engine, agregala acá.
    private static readonly (string Type, string[] Members)[] Targets =
    {
        // --- El seam de sustitución de texto (los 2 call sites son los 2 caminos del issue)
        ("Vintagestory.API.Client.KeyCombination", new[]
            { "PrimaryAsString", "SecondaryAsString", "ToString", "IsMouseButton",
              "MouseStart", "KeyCode", "SecondKeyCode", "Ctrl", "Alt", "Shift" }),
        ("Vintagestory.API.Client.HotKey", new[] { "CurrentMapping", "Code", "Name" }),

        // --- Camino VTML <hk>: el ancho se reserva en GenHotkeyTexture, antes de dibujar
        ("Vintagestory.API.Client.HotkeyComponent", new[]
            { "GenHotkeyTexture", "DrawHotkey", "hotkey", "DisplayText", "CalcBounds" }),

        // --- Íconos de mouse sin Harmony
        ("Vintagestory.API.Client.IconUtil", new[]
            { "CustomIcons", "DrawIcon", "DrawIconInt", "DrawLeftMouseButton",
              "DrawRightMouseButton" }),
        ("Vintagestory.API.Client.IconRendererDelegate", new[] { "Invoke" }),
        ("Vintagestory.API.Client.IconComponent", new[]
            { ".ctor", "ComposeElements", "CalcBounds", "sizeMulSvg" }),

        // --- Cartel flotante y el hint del ítem en mano (dos instancias del mismo tipo)
        ("Vintagestory.Client.NoObf.DrawWorldInteractionUtil", new[]
            { "drawHelp", "ActualWidth", "FontSize", "UnscaledLineHeight",
              "ComposeBlockWorldInteractionHelp" }),
        ("Vintagestory.API.Client.WorldInteraction", new[]
            { "HotKeyCode", "HotKeyCodes", "MouseButton", "ActionLangCode" }),

        // --- Medición: de acá salen todos los números de píxeles del diseño
        ("Vintagestory.API.Client.CairoFont", new[]
            { "WhiteMediumText", "WhiteSmallText", "WithFontSize", "WithStroke", "WithColor",
              "GetTextExtents", "GetFontExtents", "UnscaledFontsize", "SetupContext", "Clone" }),
        ("Vintagestory.API.Client.TextDrawUtil", new[] { "DrawTextLine" }),
        ("Vintagestory.API.Client.GuiElement", new[] { "scaled", "RoundRectangle" }),
        ("Vintagestory.API.Client.GuiStyle", new[]
            { "StandardFontName", "NormalFontSize", "SmallishFontSize", "SmallFontSize",
              "DialogDefaultTextColor", "DarkBrownColor" }),
        ("Vintagestory.API.Config.RuntimeEnv", new[] { "GUIScale" }),

        // --- Invalidación
        ("Vintagestory.Client.NoObf.GuiComposerManager", new[]
            { "MarkAllDialogsForRecompose", "RecomposeAllDialogs" }),
        ("Vintagestory.Client.NoObf.ClientMain", new[] { "GuiComposers" }),
        ("Vintagestory.Client.NoObf.ClientSettings", new[]
            { "Inst", "AddKeyCombinationUpdatedWatcher", "ClearWatchers" }),
        ("Vintagestory.API.Client.IClientEventAPI", new[] { "HotkeysChanged" }),
        ("Vintagestory.API.Client.IInputAPI", new[] { "GetHotKeyByCode", "HotKeys" }),
        ("Vintagestory.API.Client.GuiElementItemstackInfo", new[]
            { "curSlot", "SetSourceSlot" }),
    };

    public static int Run(string gameRoot, string[] args)
    {
        bool update = args.Contains("--update");
        var api = Assembly.LoadFrom(Path.Combine(gameRoot, "VintagestoryAPI.dll"));
        var lib = Assembly.LoadFrom(Path.Combine(gameRoot, "VintagestoryLib.dll"));

        var lines = new List<string>();
        foreach (var (typeName, members) in Targets)
            lines.AddRange(DumpType(typeName, members, api, lib));
        lines.AddRange(DumpValues(api, lib));
        lines.Sort(StringComparer.Ordinal);

        string baselinePath = FindBaseline();
        string current = string.Join("\n", lines) + "\n";

        Console.WriteLine($"gpclab apicheck — {api.GetName().Version} / {lib.GetName().Version}");
        Console.WriteLine($"  juego    : {gameRoot}");
        Console.WriteLine($"  baseline : {baselinePath}");
        Console.WriteLine($"  miembros : {lines.Count}");

        if (update || !File.Exists(baselinePath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(baselinePath)!);
            File.WriteAllText(baselinePath, Header(api, lib) + current);
            Console.WriteLine(File.Exists(baselinePath) ? "  baseline actualizado." : "  baseline creado.");
            return 0;
        }

        var previous = File.ReadAllLines(baselinePath)
                           .Where(l => !l.StartsWith('#') && l.Length > 0).ToList();
        var removed = previous.Except(lines, StringComparer.Ordinal).ToList();
        var added   = lines.Except(previous, StringComparer.Ordinal).ToList();

        if (removed.Count == 0 && added.Count == 0)
        {
            Console.WriteLine("  OK: la superficie de API no cambió.");
            return 0;
        }

        Console.WriteLine($"\n  CAMBIÓ la superficie de API ({removed.Count} fuera, {added.Count} nuevas):\n");
        foreach (string l in removed) Console.WriteLine("  - " + l);
        foreach (string l in added)   Console.WriteLine("  + " + l);
        Console.WriteLine(
            "\n  Revisá qué canal de glifos toca cada cambio, ajustá GlyphPatcher si hace falta,\n" +
            "  y actualizá el baseline en el MISMO commit:\n" +
            "      dotnet run --project tools/gpclab -- apicheck --update");
        return 1;
    }

    private static string Header(Assembly api, Assembly lib) =>
        "# gpclab apicheck — superficie de API del engine de la que dependen los glifos.\n" +
        "# Generado con `dotnet run --project tools/gpclab -- apicheck --update`. No editar a mano.\n" +
        $"# Tomado contra VintagestoryAPI {api.GetName().Version} / VintagestoryLib {lib.GetName().Version}.\n" +
        "# La versión del juego NO va en las líneas comparadas a propósito: un update que no\n" +
        "# toca ninguna firma tiene que pasar sin ruido, o el chequeo se vuelve costumbre.\n";

    private static IEnumerable<string> DumpType(string typeName, string[] wanted,
                                                params Assembly[] assemblies)
    {
        Type? t = assemblies.Select(a => a.GetType(typeName)).FirstOrDefault(x => x is not null);
        if (t is null)
        {
            yield return $"{typeName} :: TIPO NO ENCONTRADO";
            yield break;
        }

        var found = new HashSet<string>(StringComparer.Ordinal);
        // GetMembers ya incluye los constructores, no hay que concatenarlos aparte.
        foreach (MemberInfo m in t.GetMembers(All)
                                  .Where(m => wanted.Contains(m.Name) ||
                                              (m is ConstructorInfo && wanted.Contains(".ctor"))))
        {
            string? line = Describe(m);
            if (line is null) continue;
            found.Add(m is ConstructorInfo ? ".ctor" : m.Name);
            yield return $"{typeName} :: {line}";
        }
        foreach (string missing in wanted.Where(w => !found.Contains(w)).OrderBy(w => w, StringComparer.Ordinal))
            yield return $"{typeName} :: {missing} — MIEMBRO NO ENCONTRADO";
    }

    private static string? Describe(MemberInfo m) => m switch
    {
        MethodInfo mi when !mi.IsSpecialName => "METHOD " + Sig(mi),
        MethodInfo => null,                             // get_/set_/add_: sale por la propiedad
        ConstructorInfo ci => "CTOR   " + Access(ci) + " .ctor(" + Params(ci) + ")",
        FieldInfo fi => "FIELD  " + Access(fi) + (fi.IsStatic ? " static" : "")
                      + (fi.IsInitOnly ? " readonly" : "") + " " + Name(fi.FieldType) + " " + fi.Name,
        PropertyInfo pi => "PROP   " + Name(pi.PropertyType) + " " + pi.Name + " { "
                         + (pi.GetMethod is { } g ? Access(g) + (g.IsStatic ? " static" : "") + " get; " : "")
                         + (pi.SetMethod is { } s ? Access(s) + " set; " : "") + "}",
        EventInfo ei => "EVENT  " + Name(ei.EventHandlerType!) + " " + ei.Name,
        _ => null,
    };

    private static string Sig(MethodInfo m)
    {
        var mods = new List<string> { Access(m) };
        if (m.IsStatic) mods.Add("static");
        if (m.IsAbstract) mods.Add("abstract");
        else if (m.IsVirtual && !m.IsFinal) mods.Add("virtual");
        return string.Join(" ", mods) + " " + Name(m.ReturnType) + " " + m.Name + "(" + Params(m) + ")"
             + (m.DeclaringType is { } d ? "  [decl:" + d.Name + "]" : "");
    }

    private static string Params(MethodBase m) => string.Join(", ", m.GetParameters().Select(p =>
        (p.ParameterType.IsByRef ? (p.IsOut ? "out " : "ref ") : "")
        + Name(p.ParameterType.IsByRef ? p.ParameterType.GetElementType()! : p.ParameterType)
        + " " + p.Name
        + (p.HasDefaultValue ? " = " + (p.RawDefaultValue ?? "null") : "")));

    private static string Name(Type t) => t.IsGenericType
        ? t.Name.Split('`')[0] + "<" + string.Join(",", t.GetGenericArguments().Select(Name)) + ">"
        : t.Name;

    private static string Access(MethodBase m) =>
        m.IsPublic ? "public" : m.IsFamily ? "protected" : m.IsAssembly ? "internal"
        : m.IsFamilyOrAssembly ? "protected internal" : "private";

    private static string Access(FieldInfo f) =>
        f.IsPublic ? "public" : f.IsFamily ? "protected" : f.IsAssembly ? "internal"
        : f.IsFamilyOrAssembly ? "protected internal" : "private";

    // Valores, no firmas: de estos números salen TODAS las cuentas de píxeles del diseño, y
    // cambian sin que cambie ninguna firma. StandardFontName además decide si Cairo mide con
    // la fuente del juego o con la del sistema.
    private static IEnumerable<string> DumpValues(Assembly api, Assembly lib)
    {
        foreach (var (type, field) in new[]
        {
            ("Vintagestory.API.Client.GuiStyle", "StandardFontName"),
            ("Vintagestory.API.Client.GuiStyle", "NormalFontSize"),
            ("Vintagestory.API.Client.GuiStyle", "SmallishFontSize"),
            ("Vintagestory.API.Client.GuiStyle", "SmallFontSize"),
        })
        {
            yield return "VALUE  " + type.Split('.')[^1] + "." + field + " = " +
                         Try(() => api.GetType(type)!.GetField(field, All)!.GetValue(null));
        }

        // Los defaults de instancia del cartel. El ctor sólo asigna sus tres parámetros a
        // campos, sin dereferenciarlos, así que instanciar con nulls corre los
        // inicializadores de campo y nada más.
        yield return "VALUE  DrawWorldInteractionUtil (defaults del cartel) = " + Try(() =>
        {
            Type t = lib.GetType("Vintagestory.Client.NoObf.DrawWorldInteractionUtil")!;
            object o = Activator.CreateInstance(t, new object?[] { null, null, "-gpclab" })!;
            return "UnscaledLineHeight=" + t.GetField("UnscaledLineHeight", All)!.GetValue(o)
                 + " FontSize=" + t.GetField("FontSize", All)!.GetValue(o);
        });
    }

    private static string Try(Func<object?> f)
    {
        try { return Convert.ToString(f(), System.Globalization.CultureInfo.InvariantCulture) ?? "null"; }
        catch (Exception e) { return "<no legible: " + (e.InnerException ?? e).GetType().Name + ">"; }
    }

    // El baseline vive al lado del código fuente, no del binario: se commitea.
    private static string FindBaseline()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "gpclab.csproj")))
            dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? AppContext.BaseDirectory, "apisurface.baseline.txt");
    }
}
