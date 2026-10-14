using System;
using System.Collections.Generic;
using GamepadCompanion.Input;

namespace GamepadCompanion.Lab;

// Los mapas de botones de los layouts de PS4, contra SDL_GameControllerDB.
//
// El mod lee el joystick crudo y trae sus propios mapas, pero la referencia de
// qué botón crudo es cuál sigue siendo SDL (es la base que GLFW lleva adentro
// para su API de gamepad). Hasta 1.15.0 el mapa de Ds4Amazon (DirectInput en
// Windows) traía Guide, L3 y R3 en raw 10, 11 y 12: el orden de evdev del
// driver Sony de LINUX, copiado de SonyDs. En Windows esos tres están en
// 12, 10 y 11, así que con un DualShock 4 el botón PS agachaba (llegaba como
// R3, el toggle de Shift), R3 corría y L3 no hacía nada. Reportado por Discord
// con un DS4 v2 (054C:09CC) en Windows; nunca se había probado más que la cara
// del mando.
//
// Las líneas de abajo son las de SDL_GameControllerDB tal cual (master, oct
// 2026). Se comparan los 11 botones que el mod mapea, en el orden de
// GamepadButton.
internal static class PadMapTests
{
    private static int failures;

    private static readonly string[] SdlNames =
    {
        "a", "b", "x", "y", "leftshoulder", "rightshoulder",
        "back", "start", "guide", "leftstick", "rightstick",
    };

    // DualShock 4 v2 en Windows, DirectInput. El mando del reporte.
    private const string Ds4Windows =
        "030000004c050000cc09000000000000,PS4 Controller,a:b1,b:b2,back:b8,dpdown:h0.4," +
        "dpleft:h0.8,dpright:h0.2,dpup:h0.1,guide:b12,leftshoulder:b4,leftstick:b10," +
        "lefttrigger:a3,leftx:a0,lefty:a1,rightshoulder:b5,rightstick:b11,righttrigger:a4," +
        "rightx:a2,righty:a5,start:b9,touchpad:b13,x:b0,y:b3,platform:Windows,";

    // El mismo mando en Linux con el driver Sony del kernel (hid-playstation).
    private const string Ds4LinuxSonyDriver =
        "050000004c050000cc09000000810000,PS4 Controller,a:b0,b:b1,back:b8,dpdown:h0.4," +
        "dpleft:h0.8,dpright:h0.2,dpup:h0.1,guide:b10,leftshoulder:b4,leftstick:b11," +
        "lefttrigger:a2,leftx:a0,lefty:a1,rightshoulder:b5,rightstick:b12,righttrigger:a5," +
        "rightx:a3,righty:a4,start:b9,x:b3,y:b2,platform:Linux,";

    public static int Run()
    {
        Console.WriteLine("\n  mapas de botones de PS4 contra SDL_GameControllerDB");

        Expect("Ds4Amazon = DS4 en Windows (DirectInput)",
               GlfwGamepadProvider.Ds4AmazonButtonMap, Ds4Windows);
        Expect("SonyDs = DS4 en Linux con el driver Sony",
               GlfwGamepadProvider.SonyDsButtonMap, Ds4LinuxSonyDriver);

        // El síntoma del reporte, botón por botón.
        var ds4 = GlfwGamepadProvider.Ds4AmazonButtonMap;
        Check("DS4 en Windows: PS (raw 12) es Guide y no R3, o sea que no agacha",
              ds4[(int)GamepadButton.Guide] == 12 &&
              ds4[(int)GamepadButton.RightStick] != 12);
        Check("DS4 en Windows: L3 físico (raw 10) es L3, R3 físico (raw 11) es R3",
              ds4[(int)GamepadButton.LeftStick] == 10 &&
              ds4[(int)GamepadButton.RightStick] == 11);

        return failures;
    }

    private static void Expect(string what, int[] map, string sdlLine)
    {
        var sdl = Parse(sdlLine);
        var diffs = new List<string>();
        for (int i = 0; i < SdlNames.Length; i++)
        {
            int want = sdl.TryGetValue(SdlNames[i], out int raw) ? raw : -1;
            if (map[i] != want)
                diffs.Add($"{(GamepadButton)i}: el mod lee raw {map[i]}, SDL dice {want}");
        }
        bool ok = map.Length == SdlNames.Length && diffs.Count == 0;
        if (!ok) failures++;
        Console.WriteLine($"    [{(ok ? "ok" : "NO")}] {what}");
        foreach (string d in diffs) Console.WriteLine("           " + d);
    }

    // "a:b1" → a = 1. Sólo botones: los ejes y el hat no son de estos mapas.
    private static Dictionary<string, int> Parse(string line)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string part in line.Split(','))
        {
            int colon = part.IndexOf(':');
            if (colon < 0 || colon + 1 >= part.Length || part[colon + 1] != 'b') continue;
            if (int.TryParse(part.AsSpan(colon + 2), out int raw))
                result[part[..colon]] = raw;
        }
        return result;
    }

    private static void Check(string what, bool ok)
    {
        if (!ok) failures++;
        Console.WriteLine($"    [{(ok ? "ok" : "NO")}] {what}");
    }
}
