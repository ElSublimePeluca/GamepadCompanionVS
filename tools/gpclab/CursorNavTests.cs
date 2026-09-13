using System;
using System.Collections.Generic;
using GamepadCompanion.Input;

namespace GamepadCompanion.Lab;

// Pruebas de a dónde salta el cursor con el D-pad (issue #9), sin el juego.
//
// Las geometrías salen del reporte: la captura de nolepguy medida al píxel
// (GUIScale 1, la salida del crafteo en 1034..1081 × 542..589) y las cuentas del
// engine para lo demás (slot de 48 px, paso de 51, la salida 20 px más abajo de
// la grilla por el FixedUnder de GuiDialogInventory). Con el paso fijo de antes,
// desde y=592 las paradas eran 540 y 592: el slot entero quedaba en el medio.
internal static class CursorNavTests
{
    private static int failures;

    // Grilla de crafteo 3×3, donde cae en la captura.
    private const double GridX = 983, GridY = 369, Pitch = 51, Size = 48;

    private static readonly NavRect Output = Slot(GridX + Pitch, GridY + 3 * Pitch + 20);

    public static int Run()
    {
        Console.WriteLine("\n  destinos del D-pad (cursor virtual)");
        TheReport();
        InsideTheGrid();
        AcrossPanels();
        Cone();
        WideField();
        RecipeRow();
        Empty();
        return failures;
    }

    // ── casos ─────────────────────────────────────────────────────────────────

    private static void TheReport()
    {
        var t = Screenshot();
        Expect("↑ desde donde quedó el cursor en la captura del #9 llega a la salida",
               t, 1046, 592, NavDirection.Up, Output);
        Expect("↓ desde la columna del medio de la grilla llega a la salida",
               t, Craft(2, 1).CenterX, Craft(2, 1).CenterY, NavDirection.Down, Output);
        Expect("↓ desde la columna izquierda también llega, en diagonal",
               t, Craft(2, 0).CenterX, Craft(2, 0).CenterY, NavDirection.Down, Output);
        Expect("↑ desde la salida vuelve a la columna del medio y no a una diagonal",
               t, Output.CenterX, Output.CenterY, NavDirection.Up, Craft(2, 1));
        Expect("↓ desde la salida baja al slot del hotbar que tiene debajo",
               t, Output.CenterX, Output.CenterY, NavDirection.Down, Hotbar(8));
        Expect("desde el hueco entre la grilla y la salida, ↓ va a la salida",
               t, 1058, 530, NavDirection.Down, Output);
        Expect("y ↑ vuelve a la grilla",
               t, 1058, 530, NavDirection.Up, Craft(2, 1));
    }

    private static void InsideTheGrid()
    {
        var t = Screenshot();
        Expect("→ en la grilla va al vecino y no a la diagonal",
               t, Craft(0, 0).CenterX, Craft(0, 0).CenterY, NavDirection.Right, Craft(0, 1));
        // Adentro de un slot da igual dónde quedó el cursor: se sale por el borde.
        Expect("→ con el cursor pegado al borde derecho del slot va igual al vecino",
               t, Craft(1, 0).Right - 1, Craft(1, 0).CenterY, NavDirection.Right, Craft(1, 1));
        Expect("← con el cursor pegado al borde izquierdo vuelve al de antes",
               t, Craft(1, 1).X + 1, Craft(1, 1).CenterY, NavDirection.Left, Craft(1, 0));
    }

    private static void AcrossPanels()
    {
        var t = WithInventory();
        Expect("← desde la grilla de crafteo cruza al inventario a la misma altura",
               t, Craft(0, 0).CenterX, Craft(0, 0).CenterY, NavDirection.Left, Inv(0, 5));
        Expect("→ desde el inventario vuelve a la grilla de crafteo",
               t, Inv(0, 5).CenterX, Inv(0, 5).CenterY, NavDirection.Right, Craft(0, 0));
    }

    // Sin el cono, → desde la esquina de la grilla elegía un slot de la mochila
    // del hotbar, 600 px más abajo, por ser lo único que quedaba a la derecha.
    private static void Cone()
    {
        Expect("→ desde la esquina de la grilla no baja a la mochila del hotbar",
               Screenshot(), Craft(0, 2).CenterX, Craft(0, 2).CenterY, NavDirection.Right, null);
    }

    // Un campo ancho arriba de una grilla (la búsqueda del manual, el nombre de un
    // cofre) tiene el centro muy corrido respecto de un slot de la punta. Sin el
    // ajuste sobre el eje perpendicular quedaba afuera del cono y ganaba el botón.
    private static void WideField()
    {
        var t = WithInventory();
        var field = new NavRect(650, 320, 300, 30);
        var button = new NavRect(960, 330, 40, 20);
        t.Add(field);
        t.Add(button);
        Expect("↑ desde la punta derecha del inventario va al campo de texto de arriba",
               t, Inv(0, 5).CenterX, Inv(0, 5).CenterY, NavDirection.Up, field);
        Expect("↓ desde el campo baja al slot que tiene justo abajo",
               t, Inv(0, 2).CenterX, 335, NavDirection.Down, Inv(0, 2));
    }

    // El selector de recetas del knapping: una fila de celdas de 51 px. Con el
    // paso de 52 cada pulsación se corría 1 px y en algún momento se comía una
    // celda (la azada del reporte); acá tiene que recorrerlas todas.
    private static void RecipeRow()
    {
        var cells = new List<NavRect>();
        for (int i = 0; i < 6; i++) cells.Add(new NavRect(807 + i * Pitch, 500, Size, Size));

        double x = cells[0].CenterX, y = cells[0].CenterY;
        int visited = 0;
        for (int expected = 1; expected < cells.Count; expected++)
        {
            int i = CursorNavigation.FindNext(cells, x, y, NavDirection.Right);
            if (i != expected) break;
            visited++;
            x = cells[i].CenterX;
            y = cells[i].CenterY;
        }
        Check("→ recorre las seis celdas de a una, sin saltearse ninguna", visited == 5, $"{visited} de 5");
        Check("→ desde la última no hay a dónde ir",
              CursorNavigation.FindNext(cells, cells[5].CenterX, cells[5].CenterY, NavDirection.Right) == -1,
              null);
    }

    private static void Empty()
    {
        Check("sin destinos devuelve -1",
              CursorNavigation.FindNext(new List<NavRect>(), 100, 100, NavDirection.Down) == -1, null);
    }

    // ── geometría ─────────────────────────────────────────────────────────────

    // Lo que había en pantalla en la captura: la grilla, la salida y el hotbar (la
    // mano, los diez slots y los cuatro de la mochila). El inventario estaba vacío.
    private static List<NavRect> Screenshot()
    {
        var t = new List<NavRect>();
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                t.Add(Craft(r, c));
        t.Add(Output);
        t.Add(Slot(528, 1020));
        for (int k = 0; k < 10; k++) t.Add(Hotbar(k));
        for (int k = 0; k < 4; k++) t.Add(Slot(1155 + k * Pitch, 1020));
        return t;
    }

    // Lo mismo con bolsas en la mochila: una grilla de 6 × 7 a la izquierda.
    private static List<NavRect> WithInventory()
    {
        var t = Screenshot();
        for (int r = 0; r < 7; r++)
            for (int c = 0; c < 6; c++)
                t.Add(Inv(r, c));
        return t;
    }

    private static NavRect Slot(double x, double y) => new(x, y, Size, Size);
    private static NavRect Craft(int row, int col) => Slot(GridX + col * Pitch, GridY + row * Pitch);
    private static NavRect Hotbar(int k) => Slot(628 + k * Pitch, 1020);
    private static NavRect Inv(int row, int col) => Slot(650 + col * Pitch, 372 + row * Pitch);

    // ── helpers ───────────────────────────────────────────────────────────────

    private static void Expect(string what, List<NavRect> targets, double px, double py,
                               NavDirection dir, NavRect? expected)
    {
        int i = CursorNavigation.FindNext(targets, px, py, dir);
        NavRect? got = i >= 0 ? targets[i] : null;
        Check(what, got == expected, got is { } g ? $"({g.X}, {g.Y})" : "ninguno");
    }

    private static bool Check(string what, bool ok, string? got)
    {
        if (!ok) failures++;
        Console.WriteLine($"    [{(ok ? "ok" : "NO")}] {what}" + (ok || got is null ? "" : $"  → {got}"));
        return ok;
    }
}
