using System;
using System.Collections.Generic;
using Vintagestory.API.Client;

namespace GamepadCompanion.Input;

// D-pad con un diálogo abierto: el cursor salta al slot, celda de receta,
// pestaña o botón más cercano en esa dirección (issue #9).
//
// Es el "box selector" que PLAN.md preveía desde el principio, con la diferencia
// que libera a RB. El plan lo manejaba con el STICK derecho ("el stick salta
// entre slots"), así que hacía falta un botón para pasar ese mismo stick a cursor
// libre, y ese botón era RB: la escape hatch para las GUIs donde el box selector
// fallara. El box selector nunca se hizo (v0.1.0 le dio al D-pad un paso fijo de
// 52 px en su lugar) y RB quedó exigido aunque el stick ya no tenía otro uso en
// los diálogos. Con la navegación en el D-pad el stick no compite con nadie, y
// mueve el cursor sin RB.
public sealed class CursorNavigator
{
    private readonly ICoreClientAPI capi;
    private readonly VirtualCursor cursor;
    private readonly List<NavRect> targets = new();
    private bool reportedFailure;

    public CursorNavigator(ICoreClientAPI capi, VirtualCursor cursor)
    {
        this.capi = capi;
        this.cursor = cursor;
    }

    // Devuelve true si movió el cursor. Con el mapa a pantalla completa ↑/↓ son
    // zoom (WorldMapZoomMapper) y ←/→ conservan el paso fijo: el mapa no tiene
    // slots, y así se lo sigue pudiendo recorrer hasta un waypoint.
    public bool Apply(GamepadState current, GamepadState previous,
                      int frameW, int frameH, bool worldMap)
    {
        bool moved = false;
        if (current.WasPressed(GamepadButton.DPadLeft, previous))
            moved |= Move(NavDirection.Left, frameW, frameH, worldMap);
        if (current.WasPressed(GamepadButton.DPadRight, previous))
            moved |= Move(NavDirection.Right, frameW, frameH, worldMap);
        if (worldMap) return moved;
        if (current.WasPressed(GamepadButton.DPadUp, previous))
            moved |= Move(NavDirection.Up, frameW, frameH, worldMap: false);
        if (current.WasPressed(GamepadButton.DPadDown, previous))
            moved |= Move(NavDirection.Down, frameW, frameH, worldMap: false);
        return moved;
    }

    private bool Move(NavDirection dir, int frameW, int frameH, bool worldMap)
    {
        // Primero el control: si el usuario venía con el mouse físico, la
        // búsqueda tiene que salir de donde está el mouse.
        if (!cursor.TryTakeControl(frameW, frameH)) return false;

        if (!worldMap && CollectTargets())
        {
            int i = CursorNavigation.FindNext(targets, cursor.X, cursor.Y, dir);
            // Hay destinos pero ninguno para ese lado: el cursor se queda, como en
            // cualquier menú de consola. Mandarlo a un hueco sólo apagaría el
            // resaltado del slot en el que estaba.
            if (i < 0) return false;
            cursor.MoveTo((float)targets[i].CenterX, (float)targets[i].CenterY,
                          frameW, frameH);
            return true;
        }

        // Sin destinos: un paso del tamaño de un slot, como hacía el D-pad antes.
        // Fuera del mapa casi no pasa, porque con un diálogo abierto los slots del
        // hotbar ya cuentan como destinos; en una GUI de mod dibujada con
        // elementos propios, lo que el D-pad no alcance queda para el stick. El
        // paso va escalado, porque los 52 px fijos de antes ni siquiera seguían
        // la grilla del inventario con un GUIScale distinto de 1.
        int step = (int)Math.Round(GuiElement.scaled(
            GuiElementPassiveItemSlot.unscaledSlotSize + GuiElementItemSlotGridBase.unscaledSlotPadding));
        return dir switch
        {
            NavDirection.Left  => cursor.Step(-step, 0, frameW, frameH),
            NavDirection.Right => cursor.Step(+step, 0, frameW, frameH),
            NavDirection.Up    => cursor.Step(0, -step, frameW, frameH),
            _                  => cursor.Step(0, +step, frameW, frameH),
        };
    }

    // true si quedó al menos un destino. Se junta en cada pulsación y no por
    // frame: los diálogos cambian (scroll, pestañas, un cofre que se abre) y
    // recorrerlos cuesta nada al lado de un frame.
    private bool CollectTargets()
    {
        try
        {
            CursorTargets.Collect(capi, targets);
        }
        catch (Exception e)
        {
            // Una GUI de otro mod en un estado raro no puede llevarse puesto el
            // tick del gamepad: sin destinos, el D-pad cae al paso fijo y el stick
            // sigue andando. Se avisa una sola vez.
            targets.Clear();
            if (!reportedFailure)
            {
                reportedFailure = true;
                capi.Logger.Warning(
                    "GamepadCompanion: could not collect D-pad targets, using fixed steps instead: {0}", e);
            }
        }
        return targets.Count > 0;
    }
}
