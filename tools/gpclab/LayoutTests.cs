using System;
using System.Linq;
using System.Reflection;
using GamepadCompanion;
using GamepadCompanion.Actions;
using GamepadCompanion.Input;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace GamepadCompanion.Lab;

// Los dos presets de botones y la migración.
//
// Lo que se protege acá es una promesa concreta que se le hizo al usuario: un
// update NO le mueve los botones sin preguntar, y cambiar de preset no le borra
// nada de lo que asignó a mano. Las dos cosas sólo se ven mal cuando ya pasó.
internal static class LayoutTests
{
    private static int failures;

    public static int Run()
    {
        Console.WriteLine("\n  presets de botones");
        ICoreClientAPI capi = FakeApi();

        var classic = GamepadLayout.Build(GamepadLayoutKind.Classic, capi);
        var modern  = GamepadLayout.Build(GamepadLayoutKind.Modern,  capi);

        Check("el clásico abre la rueda con LB", classic.Wheel == GamepadButton.LeftBumper);
        Check("el nuevo la abre con D-pad ↑",    modern.Wheel  == GamepadButton.DPadUp);

        Check("clásico: hotbar en el D-pad",
              Builtin(classic, GamepadButton.DPadLeft)  == "hotbarPrev" &&
              Builtin(classic, GamepadButton.DPadRight) == "hotbarNext");
        Check("clásico: precisión en D-pad ↑ … que es el botón de nada más",
              Builtin(classic, GamepadButton.DPadUp) == "precisionToggle");
        Check("clásico: los bumpers no hacen nada (LB es la rueda)",
              classic.Default(GamepadButton.LeftBumper) is null &&
              classic.Default(GamepadButton.RightBumper) is null);

        Check("nuevo: hotbar en los bumpers",
              Builtin(modern, GamepadButton.LeftBumper)  == "hotbarPrev" &&
              Builtin(modern, GamepadButton.RightBumper) == "hotbarNext");
        Check("nuevo: precisión en D-pad ←",
              Builtin(modern, GamepadButton.DPadLeft) == "precisionToggle");
        Check("nuevo: personaje en D-pad →",
              (modern.Default(GamepadButton.DPadRight) as HotKeyAction)?.Code == "characterdialog");
        Check("nuevo: D-pad ↑ es la rueda, así que no tiene default",
              modern.Default(GamepadButton.DPadUp) is null);

        // Lo que NO se mueve: si esto cambia sin querer, se rompe la memoria
        // muscular de los dos layouts a la vez.
        foreach (var (btn, code) in new[]
                 {
                     (GamepadButton.X, "toolmodeselect"),
                     (GamepadButton.Y, "inventorydialog"),
                     (GamepadButton.Back, "worldmapdialog"),
                     (GamepadButton.Start, "escapemenudialog"),
                 })
            Check($"{btn} sigue siendo {code} en los dos",
                  (classic.Default(btn) as HotKeyAction)?.Code == code &&
                  (modern.Default(btn) as HotKeyAction)?.Code == code);
        Check("sentarse sigue en D-pad ↓ en los dos",
              Builtin(classic, GamepadButton.DPadDown) == "sitDown" &&
              Builtin(modern,  GamepadButton.DPadDown) == "sitDown");
        Check("A no tiene default en ninguno (salta desde MovementMapper)",
              classic.Default(GamepadButton.A) is null &&
              modern.Default(GamepadButton.A) is null);

        Console.WriteLine("\n  el preset no pisa lo que asignó el usuario");
        var bindings = new ButtonBindings { Layout = classic };
        var mine = new HotKeyAction("handbook", "manual");
        bindings.Set(GamepadButton.DPadRight, mine);

        Check("el binding del usuario le gana al default",
              ReferenceEquals(bindings.Effective(GamepadButton.DPadRight), mine));
        bindings.Layout = modern;
        Check("y sobrevive al cambio de preset",
              ReferenceEquals(bindings.Effective(GamepadButton.DPadRight), mine));
        Check("el resto de los botones sí toma el default nuevo",
              Builtin(bindings, GamepadButton.LeftBumper) == "hotbarPrev");

        // Un binding que quedó sobre el botón de la rueda no se borra, pero
        // tampoco se dispara: el hold ya significa abrir la rueda.
        bindings.Set(GamepadButton.DPadUp, mine);
        Check("un binding sobre el botón de la rueda queda dormido",
              bindings.Effective(GamepadButton.DPadUp) is null);
        bindings.Layout = classic;
        Check("y revive al volver al preset donde ese botón es normal",
              ReferenceEquals(bindings.Effective(GamepadButton.DPadUp), mine));
        Check("nada se perdió en el JSON",
              bindings.ToConfig().ContainsKey("DPadUp"));

        Console.WriteLine("\n  qué preset se elige al arrancar");
        Expect("instalación nueva arranca con el preset nuevo, sin preguntar",
               null, hadConfigFile: false, GamepadLayout.FreshInstall, ask: false);
        Expect("quien ya tenía config se queda con el clásico y se le pregunta",
               null, hadConfigFile: true, GamepadLayoutKind.Classic, ask: true);
        Expect("una elección guardada se respeta y no se vuelve a preguntar",
               "modern", hadConfigFile: true, GamepadLayoutKind.Modern, ask: false);
        Expect("y la otra también",
               "classic", hadConfigFile: true, GamepadLayoutKind.Classic, ask: false);
        // Un valor basura no puede tirar adentro de StartClientSide ni dejar al
        // usuario en un layout inventado.
        Expect("un valor desconocido cae en el default, sin preguntar",
               "ultra", hadConfigFile: true, GamepadLayout.FreshInstall, ask: false);

        Check("repite sólo la navegación del hotbar",
              new BuiltinAction("hotbarNext").Repeats &&
              new BuiltinAction("hotbarPrev").Repeats &&
              !new BuiltinAction("sitDown").Repeats &&
              !new BuiltinAction("precisionToggle").Repeats &&
              !new BuiltinAction("dropOrDismiss").Repeats);

        return failures;
    }

    private static void Expect(string what, string? configured, bool hadConfigFile,
                               GamepadLayoutKind kind, bool ask)
    {
        var got = GamepadCompanionModSystem.DecideLayout(configured, hadConfigFile);
        bool ok = got.Kind == kind && got.Ask == ask;
        if (!ok) failures++;
        Console.WriteLine($"    [{(ok ? "ok" : "NO")}] {what}"
                          + (ok ? "" : $"  → {got.Kind}, preguntar={got.Ask}"));
    }

    private static string? Builtin(GamepadLayout layout, GamepadButton btn)
        => (layout.Default(btn) as BuiltinAction)?.Code;

    private static string? Builtin(ButtonBindings bindings, GamepadButton btn)
        => (bindings.Effective(btn) as BuiltinAction)?.Code;

    private static void Check(string what, bool ok)
    {
        if (!ok) failures++;
        Console.WriteLine($"    [{(ok ? "ok" : "NO")}] {what}");
    }

    // Sólo hace falta GetHotKeyByCode: GamepadLayout la usa para ponerle a cada
    // default el nombre vivo de la hotkey.
    private static ICoreClientAPI FakeApi()
    {
        var input = DispatchProxy.Create<IInputAPI, Stub>();
        ((Stub)(object)input).Handler = (m, _) => m.Name == "GetHotKeyByCode"
            ? null
            : throw new NotSupportedException("IInputAPI." + m.Name);

        var capi = DispatchProxy.Create<ICoreClientAPI, Stub>();
        ((Stub)(object)capi).Handler = (m, _) => m.Name == "get_Input"
            ? input
            : throw new NotSupportedException("ICoreClientAPI." + m.Name);
        return capi;
    }
}
