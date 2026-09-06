using System;
using System.IO;
using System.Linq;
using System.Reflection;
using GamepadCompanion.Actions;
using GamepadCompanion.Input;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Path = System.IO.Path;
// Vintagestory.API.Common tambien declara un Func<,>.
using Func = System.Func<System.Reflection.MethodInfo, object?[]?, object?>;

namespace GamepadCompanion.Lab;

// Pruebas de regresión del mod que NO necesitan el juego abierto. Son dos caminos que en la
// práctica nadie ejercita a mano:
//
//   1. El round-trip de bindings a JSON. Acá vivió un bug de pérdida de datos: el switch de
//      serialización estaba duplicado en SlotBindings y en ButtonBindings, la copia de la
//      rueda nunca aprendió "holdkey", y un "mantener tecla" asignado a un slot se BORRABA
//      al guardar. Nadie lo iba a notar salvo el usuario al que se le perdió el binding.
//
//   2. La recuperación de una config corrupta. Sólo corre cuando el JSON ya está roto, o sea
//      nunca durante el desarrollo normal — y si falla, el mod entero queda inerte.
//
// Corre en la laptop, que no tiene el juego instalado: sólo hacen falta los DLL.
internal static class SelfTest
{
    private static int failures;

    public static int Run()
    {
        Console.WriteLine("gpclab selftest\n");
        SeedLang();
        RoundTrip();
        CorruptConfig();
        failures += GlyphTests.Run();
        Console.WriteLine(failures == 0
            ? "\n  todo OK"
            : $"\n  {failures} fallaron");
        return failures == 0 ? 0 : 1;
    }

    // Fuera del juego, Lang no está inicializado y Lang.Get revienta contra un diccionario
    // vacío. Sembramos un servicio que devuelve la clave: alcanza, porque acá se prueban
    // tipos y valores, no textos.
    private static void SeedLang()
    {
        var tr = DispatchProxy.Create<ITranslationService, Stub>();
        ((Stub)(object)tr).Handler = (m, _) =>
            m.ReturnType == typeof(bool) ? true
            : m.ReturnType == typeof(string) ? "(sin traducir)"
            : null;
        Lang.AvailableLanguages["en"] = tr;
        typeof(Lang).GetProperty("CurrentLocale", BindingFlags.Public | BindingFlags.Static)!
                    .SetValue(null, "en", BindingFlags.NonPublic | BindingFlags.SetProperty,
                              null, null, null);
    }

    private static void RoundTrip()
    {
        Console.WriteLine("  bindings → JSON → bindings");
        ICoreClientAPI capi = Api((m, _) => throw new NotSupportedException("ICoreClientAPI." + m.Name));

        var actions = new IGameAction?[SlotBindings.SlotCount];
        actions[4] = new HoldKeyAction((int)GlKeys.LAlt, alt: true, label: "mantener Alt");
        actions[5] = new KeyPressAction((int)GlKeys.G, label: "tecla G");
        actions[6] = new CompositeAction(new IGameAction[]
        {
            new HoldKeyAction((int)GlKeys.LShift, shift: true, label: "mantener Shift"),
            new KeyPressAction((int)GlKeys.F, label: "tecla F"),
        }, "combo");

        var saved = new SlotBindings(actions).ToConfig();
        Check("slot de rueda con holdkey se serializa", saved[4]?.Type == "holdkey", saved[4]?.Type);
        Check("conserva el keycode", saved[4]?.KeyCode == (int)GlKeys.LAlt, $"{saved[4]?.KeyCode}");
        Check("conserva el modificador", saved[4]?.AltPressed == true, $"{saved[4]?.AltPressed}");
        Check("un composite anida el holdkey", saved[6]?.Children?[0]?.Type == "holdkey",
              saved[6]?.Children?[0]?.Type);

        var back = SlotBindings.FromConfig(saved, capi);
        Check("el slot vuelve como HoldKeyAction", back[4] is HoldKeyAction, back[4]?.GetType().Name);
        // `is KeyPressAction and not HoldKeyAction` no compila, y eso ES el punto: los dos
        // tipos no están emparentados, así que la rama faltante no degradaba a tap, borraba.
        Check("el keypress de al lado no se contamina", back[5] is KeyPressAction,
              back[5]?.GetType().Name);
        Check("el holdkey adentro del composite sobrevive",
              (back[6] as CompositeAction)?.Children[0] is HoldKeyAction,
              (back[6] as CompositeAction)?.Children[0].GetType().Name);

        var buttons = new ButtonBindings();
        buttons.Set(GamepadButton.RightBumper, new HoldKeyAction((int)GlKeys.LAlt, alt: true, label: "x"));
        var bsaved = buttons.ToConfig();
        // TryGetValue y no el indexador: si la serialización vuelve a perder un tipo, la
        // entrada no está y el test tiene que REPORTARLO, no tirar una excepción.
        bsaved.TryGetValue("RightBumper", out SlotConfig? bcfg);
        Check("binding de botón con holdkey se serializa", bcfg?.Type == "holdkey",
              bcfg?.Type ?? "no quedó ninguna entrada");
        Check("y vuelve como HoldKeyAction",
              ButtonBindings.FromConfig(bsaved, capi)[GamepadButton.RightBumper] is HoldKeyAction, null);
    }

    private static void CorruptConfig()
    {
        Console.WriteLine("\n  config corrupta → arranca con defaults");
        string sandbox = Path.Combine(Path.GetTempPath(), "gpclab-cfg-" + Guid.NewGuid().ToString("N")[..8]);
        // GamePaths.DataPath es un estático de proceso y de él cuelga ModConfig. Lo apuntamos
        // a un sandbox: esto NO puede tocar la config real del usuario.
        FieldInfo dataPath = typeof(GamePaths).GetField("DataPath", BindingFlags.Public | BindingFlags.Static)!;
        object? previous = dataPath.GetValue(null);
        try
        {
            dataPath.SetValue(null, sandbox);
            Directory.CreateDirectory(GamePaths.ModConfig);
            string file = Path.Combine(GamePaths.ModConfig, "gamepadcompanion.json");
            const string broken = "{ \"YawSensitivity\": 1234, esto no es JSON válido ]";
            File.WriteAllText(file, broken);

            var logger = DispatchProxy.Create<ILogger, Stub>();
            ((Stub)(object)logger).Handler = (_, _) => null;
            ICoreClientAPI capi = Api((m, _) => m.Name switch
            {
                "get_Logger"    => logger,
                "LoadModConfig" => throw new InvalidOperationException("json roto (simulado)"),
                _ => throw new NotSupportedException("ICoreClientAPI." + m.Name),
            });

            MethodInfo? load = typeof(GamepadCompanionModSystem)
                .GetMethod("LoadConfigSafe", BindingFlags.NonPublic | BindingFlags.Static);
            if (!Check("GamepadCompanionModSystem.LoadConfigSafe sigue existiendo", load is not null, null))
                return;

            object? result = null;
            Exception? escaped = null;
            try { result = load!.Invoke(null, new object?[] { capi }); }
            catch (TargetInvocationException e) { escaped = e.InnerException; }

            Check("un JSON roto no se propaga a StartClientSide", escaped is null, escaped?.GetType().Name);
            Check("devuelve la config por defecto",
                  (result as GamepadCompanionConfig)?.YawSensitivity == new GamepadCompanionConfig().YawSensitivity,
                  $"{(result as GamepadCompanionConfig)?.YawSensitivity}");

            string[] backups = Directory.GetFiles(GamePaths.ModConfig, "gamepadcompanion.json.*.bak");
            if (Check("queda un backup del archivo ilegible", backups.Length == 1,
                      backups.Length + " archivos"))
                Check("el backup tiene el contenido original",
                      File.ReadAllText(backups[0]) == broken, "distinto");
            Check("el archivo original no se movió", File.Exists(file), "desapareció");
        }
        finally
        {
            dataPath.SetValue(null, previous);
            if (sandbox.StartsWith(Path.GetTempPath(), StringComparison.Ordinal) && Directory.Exists(sandbox))
                Directory.Delete(sandbox, recursive: true);
        }
    }

    private static ICoreClientAPI Api(Func handler)
    {
        var capi = DispatchProxy.Create<ICoreClientAPI, Stub>();
        ((Stub)(object)capi).Handler = handler;
        return capi;
    }

    private static bool Check(string what, bool ok, string? got)
    {
        if (!ok) failures++;
        Console.WriteLine($"    [{(ok ? "ok" : "NO")}] {what}" + (ok || got is null ? "" : $"  → {got}"));
        return ok;
    }
}
