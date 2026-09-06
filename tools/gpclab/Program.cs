using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace GamepadCompanion.Lab;

// gpclab — banco de pruebas OFFLINE del mod. No abre el juego: carga sus DLL y dibuja con
// el mismo Cairo, así una pregunta de píxeles o de firmas se contesta en segundos en vez
// de un reinicio completo del cliente. Nada de esto entra al mod: la carpeta tools/ está
// excluida de la compilación en GamepadCompanion.csproj.
//
//   dotnet run --project tools/gpclab -- apicheck [--update]
//   dotnet run --project tools/gpclab -- selftest
//   dotnet run --project tools/gpclab -- labels
//   dotnet run --project tools/gpclab -- boxes
//   dotnet run --project tools/gpclab -- render [salida.png]
//
// Dos trampas que hay que tener resueltas ANTES de creerle un número a esto:
//
//  1. Los DLL del juego se cargan del VINTAGE_STORY vivo, no de una copia en bin/ (ver el
//     comentario de <Private>false</Private> en el csproj).
//
//  2. GuiStyle.StandardFontName es "sans-serif", o sea que la tipografía la elige
//     fontconfig. El juego se lanza con FONTCONFIG_FILE=$VINTAGE_STORY/fonts.conf y con el
//     directorio de trabajo en la carpeta del juego (Properties/launchSettings.json), y ese
//     fonts.conf declara la carpeta de fuentes con una ruta RELATIVA. Sin las dos cosas
//     Cairo mide con el sans-serif del sistema y devuelve números plausibles y equivocados,
//     que es peor que no tener harness. Acá se replican las dos, y además se imprime una
//     huella de la fuente para que un cambio no pase inadvertido.
internal static class Program
{
    // En .NET sobre Unix, Environment.SetEnvironmentVariable escribe una copia MANEJADA del
    // entorno y no llama al setenv() de libc, así que una librería nativa que lea getenv()
    // — fontconfig — no se entera. Hay que pisarlo por P/Invoke.
    [DllImport("libc", EntryPoint = "setenv")]
    private static extern int NativeSetenv(string name, string value, int overwrite);

    private static int Main(string[] args)
    {
        string? root = ResolveGameRoot();
        if (root is null)
        {
            Console.Error.WriteLine(
                "gpclab: no encontré una instalación de Vintage Story.\n" +
                "  Probé $VINTAGE_STORY, $APPDATA/Vintagestory y ~/.local/share/vintagestory.\n" +
                "  Hace falta el juego EXTRAÍDO (no hace falta que sea jugable): exportá\n" +
                "  VINTAGE_STORY apuntando al directorio que contiene VintagestoryAPI.dll.");
            return 2;
        }

        // Antes de tocar cualquier tipo del juego: los DLL no están al lado del ejecutable.
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
        {
            string file = new AssemblyName(e.Name).Name + ".dll";
            foreach (string dir in new[] { root, Path.Combine(root, "Lib"), Path.Combine(root, "Mods") })
            {
                string path = Path.Combine(dir, file);
                if (File.Exists(path)) return Assembly.LoadFrom(path);
            }
            return null;
        };

        // cairo-sharp declara sus DllImport con el nombre de Windows ("libcairo-2") y el
        // juego resuelve eso en su propio arranque, que acá no existe. El mapeo a soname de
        // Unix lo ponemos nosotros, antes del primer P/Invoke.
        try
        {
            Assembly cairoSharp = Assembly.LoadFrom(Path.Combine(root, "Lib", "cairo-sharp.dll"));
            NativeLibrary.SetDllImportResolver(cairoSharp, ResolveNative);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("gpclab: aviso, no pude enganchar el resolver de Cairo nativo: "
                                    + e.Message);
        }

        string command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
        // Las rutas de salida se resuelven ANTES del chdir a la carpeta del juego.
        string[] rest = Array.ConvertAll(args.Length > 1 ? args[1..] : Array.Empty<string>(),
                                         a => a.StartsWith('-') ? a : Path.GetFullPath(a));
        string toolDir = AppContext.BaseDirectory;

        try
        {
            switch (command)
            {
                case "apicheck":
                    // Reflection pura: no necesita Cairo ni fuentes, así que corre incluso
                    // donde el harness gráfico no levanta.
                    return RunApiCheck(root, rest);
                case "selftest":
                    return RunSelfTest();
                case "labels":
                    SetupFonts(root);
                    return RunLabels(root);
                case "boxes":
                    SetupFonts(root);
                    return RunBoxes(root);
                case "prose":
                    SetupFonts(root);
                    return RunProse(root, rest);
                case "render":
                    SetupFonts(root);
                    return RunRender(root, rest);
                default:
                    Console.WriteLine(
                        "gpclab — banco de pruebas offline de GamepadCompanion\n\n" +
                        "  apicheck [--update]   compara las firmas del engine de las que dependen los\n" +
                        "                        glifos contra tools/gpclab/apisurface.baseline.txt\n" +
                        "  selftest              pruebas de regresión del mod que no necesitan el juego\n" +
                        "  labels                la tabla (control, familia) → etiqueta, con anchos\n" +
                        "  boxes                 mide las 3 cajas de ícono del cartel × GUIScale\n" +
                        "  render [salida.png]   dibuja líneas de cartel con la tipografía real\n\n" +
                        $"  juego: {root}\n" +
                        $"  tool : {toolDir}");
                    return command == "help" ? 0 : 1;
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("gpclab: " + e);
            return 3;
        }
    }

    // Sin inlining y en métodos aparte: el JIT resuelve los tokens de tipo al compilar el
    // método que los CONTIENE, así que si estas llamadas vivieran en Main, los tipos del
    // juego harían falta antes de que el AssemblyResolve de arriba esté enganchado.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunApiCheck(string root, string[] args) => ApiCheck.Run(root, args);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunSelfTest() => SelfTest.Run();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunLabels(string root) => Measure.Labels(root);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunBoxes(string root) => Measure.Boxes(root);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunProse(string root, string[] args) => Measure.Prose(root, args);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunRender(string root, string[] args) => Measure.Render(root, args);

    private static IntPtr ResolveNative(string library, Assembly assembly, DllImportSearchPath? path)
    {
        foreach (string candidate in NativeNames(library))
            if (NativeLibrary.TryLoad(candidate, out IntPtr handle))
                return handle;
        return IntPtr.Zero;   // que siga el resolvedor por default
    }

    // "libcairo-2" → "libcairo.so.2", "libcairo.so", "libcairo.2.dylib", …
    private static IEnumerable<string> NativeNames(string library)
    {
        string stem = library, version = "";
        int dash = library.LastIndexOf('-');
        if (dash > 0 && int.TryParse(library[(dash + 1)..], out _))
        {
            stem = library[..dash];
            version = library[(dash + 1)..];
        }
        if (OperatingSystem.IsMacOS())
        {
            if (version.Length > 0) yield return $"{stem}.{version}.dylib";
            yield return $"{stem}.dylib";
        }
        else if (!OperatingSystem.IsWindows())
        {
            if (version.Length > 0) yield return $"{stem}.so.{version}";
            yield return $"{stem}.so";
        }
        yield return library;
    }

    private static string? ResolveGameRoot()
    {
        string?[] candidates =
        {
            Environment.GetEnvironmentVariable("VINTAGE_STORY"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vintagestory"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                         ".local", "share", "vintagestory"),
        };
        foreach (string? candidate in candidates)
            if (!string.IsNullOrWhiteSpace(candidate) &&
                File.Exists(Path.Combine(candidate, "VintagestoryAPI.dll")))
                return Path.GetFullPath(candidate);
        return null;
    }

    // Replica exacta de cómo se lanza el juego (launchSettings.json). Devuelve lo que quedó
    // configurado para que los comandos lo impriman: un harness de píxeles que mintió sobre
    // la fuente es peor que no tener harness.
    internal static string SetupFonts(string root)
    {
        if (OperatingSystem.IsWindows())
            return "(Windows: Cairo no usa fontconfig)";

        string conf = Path.Combine(root, "fonts.conf");
        if (!File.Exists(conf))
            return $"(falta {conf}: se mide con el sans-serif del sistema)";

        // El fonts.conf del juego declara <dir>assets/game/fonts</dir> con ruta RELATIVA,
        // así que sólo resuelve con el cwd en el directorio del juego.
        Directory.SetCurrentDirectory(root);
        Environment.SetEnvironmentVariable("FONTCONFIG_FILE", conf);
        try
        {
            if (NativeSetenv("FONTCONFIG_FILE", conf, 1) != 0)
                return $"(setenv falló para {conf})";
        }
        catch (Exception e)
        {
            return $"(no pude exportar FONTCONFIG_FILE: {e.GetType().Name})";
        }
        return conf;
    }
}
