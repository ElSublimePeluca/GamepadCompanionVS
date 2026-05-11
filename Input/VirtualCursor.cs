using System;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;

namespace GamepadCompanion.Input;

// Estado del cursor virtual del gamepad. Se activa cuando el usuario
// mantiene RB con un GuiDialog abierto (DialogType=Dialog). Posición en
// pixels relativos al framebuffer; el origen es top-left.
public sealed class VirtualCursor
{
    // Velocidad máxima del cursor a stick a tope (px/seg). Suficiente para
    // cruzar una pantalla 1080p en ~0.9s.
    private const float MaxSpeed = 1200f;
    private const float DeadZone = 0.15f;

    private readonly ICoreClientAPI capi;

    public float X { get; private set; }
    public float Y { get; private set; }
    public bool  Visible { get; private set; }

    public VirtualCursor(ICoreClientAPI capi)
    {
        this.capi = capi;
    }

    // Llama al activarse el cursor (RB+Dialog). Si ya estaba visible no
    // mueve, así si el usuario suelta y vuelve a presionar RB en sucesión
    // corta no pierde la posición.
    public void Show(int frameW, int frameH)
    {
        if (!Visible)
        {
            X = frameW / 2f;
            Y = frameH / 2f;
            Visible = true;
        }
    }

    public void Hide() => Visible = false;

    public void Update(float stickX, float stickY, float dt,
                       int frameW, int frameH)
    {
        if (!Visible) return;

        // Curva cuadrática suave: precisión cerca del centro, velocidad
        // cerca del borde. Sign(s)*s² conserva dirección.
        float ax = ApplyDeadZone(stickX);
        float ay = ApplyDeadZone(stickY);

        X = Math.Clamp(X + ax * MaxSpeed * dt,        0, frameW);
        // Y invertido: stick arriba (+Y) sube en pantalla (-Y).
        Y = Math.Clamp(Y - ay * MaxSpeed * dt,        0, frameH);

        SyncOsCursor();
    }

    // Salto discreto en pixels (DPad navega slot-por-slot del inventario
    // sin tener que arrastrar con el stick). El caller decide la magnitud
    // según el contexto (típicamente el ancho aproximado de un slot UI).
    public void Step(int dx, int dy, int frameW, int frameH)
    {
        if (!Visible) return;
        X = Math.Clamp(X + dx, 0, frameW);
        Y = Math.Clamp(Y + dy, 0, frameH);
        SyncOsCursor();
    }

    // Re-sincroniza la posición OS/ClientMain sin mover el cursor. Útil
    // cuando el cursor está visible pero no se movió este tick: algunos
    // widgets (HudDropItem para el item arrastrado, etc) leen estado que
    // sólo se refresca via GLFW callback, y sin un SetCursorPos por frame
    // ese estado puede driftear. En modo step (DPad), entre pasos el
    // cursor.X/Y no cambia pero llamamos Sync para que esos widgets no
    // se queden con la posición del mouse físico al abrirse el dialog.
    public void Sync()
    {
        if (!Visible) return;
        SyncOsCursor();
    }

    // Sincroniza la posición del mouse a varios niveles para que los
    // widgets que la leen por distintas vías encuentren el cursor
    // virtual:
    //
    //  - GLFW.SetCursorPos: mueve el cursor del OS, así si el usuario
    //    suelta RB el mouse físico queda donde estaba el virtual.
    //  - ClientMain.MouseCurrentX/Y: campos públicos que `capi.Input.
    //    MouseX/Y` exponen. Algunos widgets (GuiElementSkillItemGrid,
    //    usado por el recipe selector del knapping/anvil/tool-mode)
    //    los leen directo en RenderInteractiveElements/OnMouseDownOnElement,
    //    ignorando args.X/Y del MouseEvent. Sin escribirlos a mano el
    //    hover y click no funcionan en esos widgets.
    private unsafe void SyncOsCursor()
    {
        var window = GLFW.GetCurrentContext();
        if (window != null) GLFW.SetCursorPos(window, X, Y);

        if (capi.World is ClientMain cm)
        {
            cm.MouseCurrentX = (int)X;
            cm.MouseCurrentY = (int)Y;
        }
    }

    private static float ApplyDeadZone(float v)
    {
        float abs = MathF.Abs(v);
        if (abs < DeadZone) return 0f;
        float scaled = (abs - DeadZone) / (1f - DeadZone);
        return MathF.Sign(v) * scaled * scaled;
    }
}
