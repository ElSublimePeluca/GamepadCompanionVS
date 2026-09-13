using System;
using System.Collections.Generic;

namespace GamepadCompanion.Input;

internal enum NavDirection { Left, Right, Up, Down }

// Rectángulo en píxeles de framebuffer: el mismo espacio que el cursor virtual y
// que ElementBounds.absX/absY. Es un tipo nuestro y no un ElementBounds a
// propósito, porque el algoritmo de abajo no toca el engine y así gpclab lo
// prueba sin el juego.
internal readonly record struct NavRect(double X, double Y, double W, double H)
{
    public double Right   => X + W;
    public double Bottom  => Y + H;
    public double CenterX => X + W / 2;
    public double CenterY => Y + H / 2;

    // Bordes incluidos, igual que ElementBounds.PointInside: es lo que decide el
    // hover en el engine, y el origen tiene que ser lo que el jugador ve resaltado.
    public bool Contains(double px, double py)
        => px >= X && px <= Right && py >= Y && py <= Bottom;
}

// Elige a dónde salta el cursor con el D-pad: el destino más cercano en esa
// dirección entre los que junta CursorTargets (slots, celdas, botones).
//
// Reemplaza al paso fijo de 52 px, que no miraba la UI (issue #9). La salida del
// crafteo cae 23 px debajo de la grilla, así que sus 48 px podían entrar enteros
// entre dos paradas y no había forma de llegar apretando.
//
// El puntaje es el de Selectable.FindSelectable de Unity: avance en la dirección
// dividido por la distancia al cuadrado, o sea que gana lo más cercano y más
// alineado a la vez. Con dos ajustes para las GUIs de VS:
//   - Se mide desde el BORDE del origen y no desde el cursor: adentro de un slot
//     da igual si el cursor quedó a la izquierda o a la derecha del centro.
//   - Sobre el eje perpendicular, un candidato que cubre al origen cuenta como
//     alineado: un campo de texto ancho justo arriba de la grilla no pierde
//     contra un slot chico sólo por tener el centro corrido.
internal static class CursorNavigation
{
    // Lo mínimo que tiene que avanzar un candidato para no ser "el mismo lugar".
    private const double MinAdvance = 1.0;

    // Cono de búsqueda: el desvío perpendicular puede ser a lo sumo 1,75 veces el
    // avance, unos 60°. Sin el cono, → desde la esquina de la grilla de crafteo
    // elegía un slot de la mochila 600 px más abajo, por ser lo único que quedaba
    // a la derecha. Con 60° todavía entra la salida del crafteo vista desde la
    // columna izquierda de la grilla (49°), que es el caso del issue.
    private const double MaxSlope = 1.75;

    // Índice del destino elegido, o -1 si no hay ninguno para ese lado.
    public static int FindNext(IReadOnlyList<NavRect> targets, double px, double py,
                               NavDirection dir)
    {
        // Origen: el destino más chico que contiene al cursor. Si no está sobre
        // ninguno (el stick lo dejó en un hueco), el punto mismo.
        int from = -1;
        double fromArea = double.MaxValue;
        for (int i = 0; i < targets.Count; i++)
        {
            NavRect t = targets[i];
            if (t.Contains(px, py) && t.W * t.H < fromArea)
            {
                from = i;
                fromArea = t.W * t.H;
            }
        }
        NavRect src = from >= 0 ? targets[from] : new NavRect(px, py, 0, 0);

        (double dx, double dy) = dir switch
        {
            NavDirection.Left  => (-1.0, 0.0),
            NavDirection.Right => (1.0, 0.0),
            NavDirection.Up    => (0.0, -1.0),
            _                  => (0.0, 1.0),
        };

        // Punto de salida: el centro del borde del origen que mira hacia allá.
        double ex = dx > 0 ? src.Right  : dx < 0 ? src.X : src.CenterX;
        double ey = dy > 0 ? src.Bottom : dy < 0 ? src.Y : src.CenterY;

        int best = -1;
        double bestScore = 0;
        for (int i = 0; i < targets.Count; i++)
        {
            if (i == from) continue;
            NavRect t = targets[i];

            double rx = dx != 0 ? t.CenterX : Math.Clamp(ex, t.X, t.Right);
            double ry = dy != 0 ? t.CenterY : Math.Clamp(ey, t.Y, t.Bottom);
            double vx = rx - ex;
            double vy = ry - ey;

            double along  = vx * dx + vy * dy;
            double across = Math.Abs(vx * dy - vy * dx);
            if (along < MinAdvance || across > along * MaxSlope) continue;

            double score = along / (vx * vx + vy * vy);
            if (score > bestScore)
            {
                bestScore = score;
                best = i;
            }
        }
        return best;
    }
}
