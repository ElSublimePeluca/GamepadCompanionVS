using System;
using GamepadCompanion.Input;

namespace GamepadCompanion.Lab;

// A y RT son el mismo click izquierdo del cursor virtual. Lo que se prueba es que
// se comporten como UN botón: con dos botones separados, apretar A con RT ya
// apretado mandaría un segundo MouseDown sin Up en el medio.
internal static class CursorClickTests
{
    private static int failures;

    public static int Run()
    {
        Console.WriteLine("\n  clic del cursor virtual (A y RT)");

        GamepadState idle = Pad(), rt = Pad(rt: 1f), a = Pad(a: true), both = Pad(a: true, rt: 1f);

        Expect("A sola da un clic", true, (1, 1), idle, a, idle);
        Expect("RT solo da un clic", true, (1, 1), idle, rt, idle);
        Expect("RT y después A, soltando RT primero, siguen siendo un solo clic",
               true, (1, 1), idle, rt, both, a, idle);
        Expect("A y RT en el mismo frame también", true, (1, 1), idle, both, idle);
        Expect("el gatillo a medio apretar no clickea", true, (0, 0), idle, Pad(rt: 0.3f), idle);

        Expect("con un binding en A, A no clickea", false, (0, 0), idle, a, idle);
        Expect("y RT sigue clickeando", false, (1, 1), idle, rt, idle);
        return failures;
    }

    // Cuántos MouseDown y MouseUp izquierdos saldrían de esa secuencia de frames,
    // con la misma detección de flancos que CursorClickMapper.Apply.
    private static void Expect(string what, bool aClicks, (int Downs, int Ups) expected,
                               params GamepadState[] frames)
    {
        int downs = 0, ups = 0;
        for (int i = 1; i < frames.Length; i++)
        {
            bool now  = CursorClickMapper.LeftDown(frames[i], aClicks);
            bool prev = CursorClickMapper.LeftDown(frames[i - 1], aClicks);
            if (now && !prev) downs++;
            if (!now && prev) ups++;
        }
        bool ok = (downs, ups) == expected;
        if (!ok) failures++;
        Console.WriteLine($"    [{(ok ? "ok" : "NO")}] {what}" + (ok ? "" : $"  → {downs} down, {ups} up"));
    }

    private static GamepadState Pad(bool a = false, float rt = 0f)
        => new((ushort)(a ? 1 << (int)GamepadButton.A : 0), 0, 0, 0, 0, 0, rt);
}
