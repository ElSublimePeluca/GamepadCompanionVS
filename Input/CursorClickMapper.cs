using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace GamepadCompanion.Input;

// Inyecta clicks UI desde la posición del cursor virtual. Activo solo
// cuando cursor.Visible, o sea con un diálogo modal abierto.
//
// Convención de triggers: RT → click izquierdo, LT → click derecho.
// Es la misma asignación que TriggerMapper usa in-world (RT=atacar,
// LT=interactuar), elegida a propósito para que el usuario no tenga
// que recablear su reflejo entre modos.
//
// A también es click izquierdo, para el flujo de consola: el pulgar izquierdo
// salta entre slots con el D-pad y el derecho confirma con A. RT sigue siendo
// el click de apuntar con el stick derecho, porque pulgar e índice pueden mover
// y mantener el click a la vez (arrastrar para repartir), y A está bajo el mismo
// pulgar que el stick. Si el usuario le asignó algo a A, manda su binding y el
// click queda sólo en RT.
//
// Los clicks salen por dos lados: a los GuiDialog, acá mismo, como MouseEvent; y
// a los botones del MouseState de OpenTK, que es lo único que lee ImGui. Eso
// segundo lo escribe NativeMouseMirror con LeftHeld/RightHeld desde el `finally`
// del driver.
//
// MouseMove se emite cada tick (incluso si el stick no se movió) para
// que el hover state se actualice y los sliders/drags reaccionen entre
// Down y Up. OnMouseDown/Up usan edge-detect contra el tick previo.
public sealed class CursorClickMapper
{
    private const float Threshold = 0.5f;

    private readonly ICoreClientAPI capi;
    private readonly VirtualCursor cursor;
    private readonly ButtonMapper buttons;

    // Botones del cursor apretados en este tick. El driver los limpia al empezar
    // cada tick, así un frame sin cursor (se cerró el diálogo, radial, foco
    // perdido) los deja en false y el espejo de OpenTK suelta solo.
    public bool LeftHeld  { get; private set; }
    public bool RightHeld { get; private set; }

    public CursorClickMapper(ICoreClientAPI capi, VirtualCursor cursor,
                             ButtonMapper buttons)
    {
        this.capi = capi;
        this.cursor = cursor;
        this.buttons = buttons;
    }

    public void ClearHeld() => LeftHeld = RightHeld = false;

    public void Apply(GamepadState current, GamepadState previous)
    {
        if (!cursor.Visible) return;

        int x = (int)cursor.X;
        int y = (int)cursor.Y;

        DispatchMove(x, y);

        bool aClicks = buttons.Bindings[GamepadButton.A] is null;
        LeftHeld  = LeftDown(current, aClicks);
        RightHeld = current.LeftTrigger > Threshold;
        EdgeDispatch(LeftHeld, LeftDown(previous, aClicks),
                     EnumMouseButton.Left, x, y);
        EdgeDispatch(RightHeld, previous.LeftTrigger > Threshold,
                     EnumMouseButton.Right, x, y);
    }

    // RT y A son un único botón izquierdo: el click baja con el primero que se
    // aprieta y sube cuando se sueltan los dos. Como dos botones separados,
    // apretar A con RT ya apretado mandaría un segundo MouseDown sin Up en el
    // medio, y el slot lo tomaría como otro click.
    internal static bool LeftDown(GamepadState state, bool aClicks)
        => state.RightTrigger > Threshold
           || (aClicks && state.IsDown(GamepadButton.A));

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

    private void EdgeDispatch(bool nowDown, bool prevDown, EnumMouseButton btn,
                              int x, int y)
    {
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
