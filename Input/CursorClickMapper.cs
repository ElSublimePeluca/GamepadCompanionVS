using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace GamepadCompanion.Input;

// Inyecta clicks UI desde la posición del cursor virtual. Activo solo
// cuando cursor.Visible (modo cursor: RB + dialog abierto).
//
// Convención de triggers: RT → click izquierdo, LT → click derecho.
// Es la misma asignación que TriggerMapper usa in-world (RT=atacar,
// LT=interactuar), elegida a propósito para que el usuario no tenga
// que recablear su reflejo entre modos.
//
// MouseMove se emite cada tick (incluso si el stick no se movió) para
// que el hover state se actualice y los sliders/drags reaccionen entre
// Down y Up. OnMouseDown/Up usan edge-detect contra el tick previo.
public sealed class CursorClickMapper
{
    private const float Threshold = 0.5f;

    private readonly ICoreClientAPI capi;
    private readonly VirtualCursor cursor;

    public CursorClickMapper(ICoreClientAPI capi, VirtualCursor cursor)
    {
        this.capi = capi;
        this.cursor = cursor;
    }

    public void Apply(GamepadState current, GamepadState previous)
    {
        if (!cursor.Visible) return;

        int x = (int)cursor.X;
        int y = (int)cursor.Y;

        DispatchMove(x, y);

        EdgeDispatch(current.RightTrigger, previous.RightTrigger,
                     EnumMouseButton.Left,  x, y);
        EdgeDispatch(current.LeftTrigger,  previous.LeftTrigger,
                     EnumMouseButton.Right, x, y);
    }

    private void DispatchMove(int x, int y)
    {
        foreach (var dlg in capi.Gui.LoadedGuis)
        {
            if (dlg is null || !dlg.ShouldReceiveMouseEvents()) continue;
            var ev = new MouseEvent(x, y, EnumMouseButton.None, 0);
            dlg.OnMouseMove(ev);
            if (ev.Handled) break;
        }
    }

    private void EdgeDispatch(float now, float prev, EnumMouseButton btn,
                              int x, int y)
    {
        bool nowDown  = now  > Threshold;
        bool prevDown = prev > Threshold;
        if (nowDown == prevDown) return;

        foreach (var dlg in capi.Gui.LoadedGuis)
        {
            if (dlg is null || !dlg.ShouldReceiveMouseEvents()) continue;
            // MouseEvent fresco por dialog para que Handled empiece en false;
            // si un dialog lo consume, break, los demás no lo ven.
            var ev = new MouseEvent(x, y, btn, 0);
            if (nowDown) dlg.OnMouseDown(ev);
            else         dlg.OnMouseUp(ev);
            if (ev.Handled) break;
        }
    }
}
