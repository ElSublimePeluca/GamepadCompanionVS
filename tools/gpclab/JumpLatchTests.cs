using System;
using GamepadCompanion.Input;

namespace GamepadCompanion.Lab;

// El salto de A no puede ser un nivel puro.
//
// Desde 1.13.0 A también clickea en los diálogos, y varios diálogos del juego se
// cierran en el MouseDOWN: GuiElementSkillItemGrid invoca OnSlotClick desde
// OnMouseDownOnElement, y ahí adentro GuiDialogToolMode (el default de X) y
// GuiDialogBlockEntityRecipeSelector (knapping, alfarería, yunque) llaman a
// TryClose(). O sea que el diálogo desaparece con A todavía apretada y el frame
// siguiente ya tiene permiso de saltar: elegir un modo de herramienta o una
// receta hacía saltar al personaje ("my guy jumps when I back out of menus", en
// ModDB contra 1.13.x).
//
// Lo que se fija acá es la regla del latch, sin el juego: hace falta soltar A y
// volver a apretarla, y mantenerla apretada en el mundo tiene que seguir
// saltando (el engine relee el flag en cada tick para saltar al aterrizar).
internal static class JumpLatchTests
{
    private static int failures;

    public static int Run()
    {
        Console.WriteLine("\n  salto de A a través de un diálogo");

        Expect("A en el mundo salta", 1, F(a: true, jumpOk: true));
        Expect("mantenerla apretada sigue saltando", 3,
               F(true, true), F(true, true), F(true, true));

        Expect("con un diálogo abierto no salta", 0,
               F(true, false), F(true, false));

        // El caso del reporte: el diálogo se cierra en el MouseDown de A.
        Expect("el diálogo que se cierra con A apretada no deja un salto", 0,
               F(true, false), F(true, true), F(true, true), F(true, true));

        Expect("soltar y volver a apretar vuelve a saltar", 1,
               F(true, false), F(true, true), F(false, true), F(true, true));

        // Release() del driver (sin foco, pad desconectado) entra al latch.
        ExpectFrom(true, "volver de un alt-tab con A apretada no salta", 0,
                   F(true, true), F(true, true));
        ExpectFrom(true, "y salta de nuevo apenas se suelta y se aprieta", 1,
                   F(true, true), F(false, true), F(true, true));

        return failures;
    }

    private static (bool A, bool JumpOk) F(bool a, bool jumpOk) => (a, jumpOk);

    // Cuántos frames de la secuencia proyectarían "jump" a KeyboardState, con la
    // misma regla que MovementMapper.Apply.
    private static void ExpectFrom(bool latched, string what, int expected,
                                   params (bool A, bool JumpOk)[] frames)
    {
        int jumps = 0;
        foreach (var (a, jumpOk) in frames)
        {
            latched = MovementMapper.JumpLatched(latched, a, jumpOk);
            if (a && jumpOk && !latched) jumps++;
        }
        bool ok = jumps == expected;
        if (!ok) failures++;
        Console.WriteLine($"    [{(ok ? "ok" : "NO")}] {what}"
                          + (ok ? "" : $"  → {jumps} frames con salto, esperaba {expected}"));
    }

    private static void Expect(string what, int expected,
                               params (bool A, bool JumpOk)[] frames)
        => ExpectFrom(false, what, expected, frames);
}
