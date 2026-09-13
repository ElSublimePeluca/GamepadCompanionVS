using System;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;

namespace GamepadCompanion.Input;

// Estado del cursor virtual del gamepad. Se activa cuando hay un GuiDialog
// (modal) abierto. Posición en pixels relativos al framebuffer; el origen es
// top-left.
//
// Convivencia con el mouse FÍSICO: el cursor virtual escribía SetCursorPos cada
// frame, lo que "clavaba" el mouse real — el usuario no podía soltar el control
// y tirar la mano al mouse (reportado por ElSublimePeluca). Ahora:
//   - Solo escribimos al OS en frames donde el gamepad efectivamente movió el
//     cursor (stick derecho, o salto del D-pad). En frames idle no tocamos el
//     mouse, así el mouse físico queda libre.
//   - Si detectamos que el cursor del OS se movió sin que lo movamos nosotros
//     (el usuario agarró el mouse), entramos en PhysicalOverride: escondemos el
//     cursor amarillo y dejamos de sincronizar hasta que el gamepad vuelva a
//     pedir el control (y ahí re-anclamos donde está el mouse, sin saltos).
//   - Si la ventana de VS pierde el foco (alt-tab), no tocamos el mouse del OS
//     en absoluto — antes seguía snapeando el mouse a la posición de VS aunque
//     VS no estuviera visible.
public sealed class VirtualCursor
{
    // Velocidad máxima del cursor a stick a tope (px/seg). Suficiente para
    // cruzar una pantalla 1080p en ~0.9s.
    private const float MaxSpeed = 1200f;

    // Inclinación mínima del stick para quitarle el control al mouse físico.
    // Desde el issue #9 el stick mueve el cursor sin mantener RB, así que un mando
    // con deriva apoyado en el escritorio pelearía con el mouse mientras haya un
    // diálogo abierto. Pasar la zona muerta alcanza para mover un cursor que ya es
    // del gamepad; para arrebatárselo al mouse hace falta una inclinación que no
    // pueda ser deriva.
    private const float ReclaimDeflection = 0.5f;

    // Umbral (px) de desplazamiento del cursor del OS que NO causamos nosotros
    // para considerar que el usuario agarró el mouse físico. 3px filtra el
    // redondeo a int de SetCursorPos sin perder movimiento real.
    private const double PhysicalMoveThreshold = 3.0;

    private readonly ICoreClientAPI capi;

    public float X { get; private set; }
    public float Y { get; private set; }
    public bool  Visible { get; private set; }

    // El usuario tomó el mouse físico: escondemos el cursor virtual (el renderer
    // lo chequea) y dejamos de forzar SetCursorPos para no pelear con el mouse.
    // Se limpia cuando el gamepad vuelve a mover el cursor.
    public bool PhysicalOverride { get; private set; }

    // Última posición que ESCRIBIMOS al OS. Si en un frame el cursor del OS
    // difiere de esto, el movimiento vino del mouse físico.
    private double lastSyncX;
    private double lastSyncY;
    private bool   haveSynced;

    public VirtualCursor(ICoreClientAPI capi)
    {
        this.capi = capi;
    }

    // Se llama en cada frame con un diálogo abierto y sólo inicializa la primera
    // vez: la posición sobrevive mientras quede algún diálogo. Al mostrarse
    // arranca donde está el mouse físico (no en el centro) y establece ese punto
    // como baseline: así no hay salto al abrir el dialog y la detección de mouse
    // físico tiene una referencia inmediata.
    public unsafe void Show(int frameW, int frameH)
    {
        if (Visible) return;

        var window = GLFW.GetCurrentContext();
        if (window != null)
        {
            GLFW.GetCursorPos(window, out double cx, out double cy);
            X = (float)Math.Clamp(cx, 0, frameW);
            Y = (float)Math.Clamp(cy, 0, frameH);
            lastSyncX = X;
            lastSyncY = Y;
            haveSynced = true;
        }
        else
        {
            X = frameW / 2f;
            Y = frameH / 2f;
            haveSynced = false;
        }

        Visible = true;
        PhysicalOverride = false;
    }

    public void Hide()
    {
        Visible = false;
        PhysicalOverride = false;
        haveSynced = false;
    }

    // Stick derecho → movimiento libre. Devuelve true si movió el cursor, y en ese
    // caso ya sincronizó el mouse del OS. La zona muerta es la del config: quien
    // tiene deriva ya la subió para la cámara, y ahora el stick está vivo en todos
    // los diálogos.
    public unsafe bool Update(float stickX, float stickY, float deadZone, float dt,
                              int frameW, int frameH)
    {
        if (!Visible) return false;

        var window = GLFW.GetCurrentContext();
        // Sin foco no tocamos el mouse del OS (fix alt-tab): antes seguíamos
        // snapeando el mouse a VS aunque estuviera en segundo plano.
        if (!IsFocused(window)) return false;

        // Un config editado a mano con 1.0 dividiría por cero en la curva.
        deadZone = Math.Clamp(deadZone, 0f, 0.9f);

        // Curva cuadrática suave: precisión cerca del centro, velocidad cerca
        // del borde. Sign(s)*s² conserva dirección.
        float ax = ApplyDeadZone(stickX, deadZone);
        float ay = ApplyDeadZone(stickY, deadZone);
        bool deliberate = !PhysicalOverride
            || stickX * stickX + stickY * stickY >= ReclaimDeflection * ReclaimDeflection;

        if ((ax != 0f || ay != 0f) && deliberate)
        {
            // El gamepad pide el control: si veníamos cediéndoselo al mouse
            // físico, re-anclamos donde está el mouse para no saltar.
            if (PhysicalOverride) ReanchorToOsCursor(window, frameW, frameH);
            PhysicalOverride = false;

            X = Math.Clamp(X + ax * MaxSpeed * dt, 0, frameW);
            // Y invertido: stick arriba (+Y) sube en pantalla (-Y).
            Y = Math.Clamp(Y - ay * MaxSpeed * dt, 0, frameH);
            SyncOsCursor(window);
            return true;
        }

        // Stick quieto y el mouse del OS se movió por su cuenta → el usuario
        // agarró el mouse. Cedemos.
        if (PhysicalMoved(window)) PhysicalOverride = true;
        return false;
    }

    // El D-pad es input de gamepad, así que toma el control antes de decidir
    // nada: si el usuario venía con el mouse físico, la búsqueda del destino
    // tiene que arrancar donde está el mouse y no donde quedó el cursor amarillo.
    // Devuelve false si no hay cursor o la ventana no tiene foco.
    public unsafe bool TryTakeControl(int frameW, int frameH)
    {
        if (!Visible) return false;

        var window = GLFW.GetCurrentContext();
        if (!IsFocused(window)) return false;

        if (PhysicalOverride) ReanchorToOsCursor(window, frameW, frameH);
        PhysicalOverride = false;
        return true;
    }

    // Salto absoluto: el centro del destino que eligió CursorNavigation. Va
    // después de TryTakeControl, que es quien chequea foco y visibilidad.
    public unsafe void MoveTo(float x, float y, int frameW, int frameH)
    {
        X = Math.Clamp(x, 0, frameW);
        Y = Math.Clamp(y, 0, frameH);
        SyncOsCursor(GLFW.GetCurrentContext());
    }

    // Salto relativo en pixels: lo que queda del paso fijo del D-pad, para cuando
    // no hay destinos que reconocer (el mapa, la GUI de algún mod).
    public bool Step(int dx, int dy, int frameW, int frameH)
    {
        if (!TryTakeControl(frameW, frameH)) return false;
        MoveTo(X + dx, Y + dy, frameW, frameH);
        return true;
    }

    // Re-sincroniza la posición OS/ClientMain sin mover el cursor. En frames sin
    // movimiento el cursor.X/Y no cambia pero algunos widgets (HudDropItem
    // para el item arrastrado, etc) leen estado que sólo se refresca via GLFW
    // callback y sin un SetCursorPos por frame puede driftear. Pero si el usuario
    // está usando el mouse físico (PhysicalOverride o movimiento detectado) NO
    // sincronizamos: pelearía con el mouse.
    public unsafe void Sync()
    {
        if (!Visible) return;

        var window = GLFW.GetCurrentContext();
        if (!IsFocused(window)) return;

        if (PhysicalMoved(window)) { PhysicalOverride = true; return; }
        if (PhysicalOverride) return;

        SyncOsCursor(window);
    }

    // Sincroniza la posición del mouse a varios niveles para que los widgets que
    // la leen por distintas vías encuentren el cursor virtual:
    //
    //  - GLFW.SetCursorPos: mueve el cursor del OS, así si el usuario agarra el
    //    mouse físico lo encuentra donde estaba el virtual.
    //  - ClientMain.MouseCurrentX/Y: campos públicos que `capi.Input.MouseX/Y`
    //    exponen. Algunos widgets (GuiElementSkillItemGrid, usado por el recipe
    //    selector del knapping/anvil/tool-mode) los leen directo en
    //    RenderInteractiveElements/OnMouseDownOnElement, ignorando args.X/Y del
    //    MouseEvent. Sin escribirlos a mano el hover y click no funcionan ahí.
    //
    // Registramos lastSync para poder distinguir después nuestro propio
    // movimiento del que mete el mouse físico.
    private unsafe void SyncOsCursor(Window* window)
    {
        if (window != null) GLFW.SetCursorPos(window, X, Y);

        lastSyncX = X;
        lastSyncY = Y;
        haveSynced = true;

        if (capi.World is ClientMain cm)
        {
            cm.MouseCurrentX = (int)X;
            cm.MouseCurrentY = (int)Y;
        }
    }

    // ¿El cursor del OS se movió respecto a lo último que escribimos? Si nunca
    // sincronizamos no hay baseline, así que no afirmamos movimiento.
    private unsafe bool PhysicalMoved(Window* window)
    {
        if (!haveSynced || window == null) return false;
        GLFW.GetCursorPos(window, out double cx, out double cy);
        double dx = cx - lastSyncX;
        double dy = cy - lastSyncY;
        return dx * dx + dy * dy > PhysicalMoveThreshold * PhysicalMoveThreshold;
    }

    // Mueve el cursor virtual a donde está el mouse físico ahora. Se llama al
    // retomar el control tras un PhysicalOverride para que el cursor amarillo
    // aparezca bajo la mano del usuario, sin saltar a su posición previa.
    //
    // También mueve la referencia de PhysicalMoved. Sin eso, un D-pad que retoma
    // el control pero no encuentra destino dejaba la referencia vieja, y el Sync
    // del mismo frame volvía a ver "el mouse se movió" y devolvía el control.
    private unsafe void ReanchorToOsCursor(Window* window, int frameW, int frameH)
    {
        if (window == null) return;
        GLFW.GetCursorPos(window, out double cx, out double cy);
        X = (float)Math.Clamp(cx, 0, frameW);
        Y = (float)Math.Clamp(cy, 0, frameH);
        lastSyncX = cx;
        lastSyncY = cy;
        haveSynced = true;
    }

    private static unsafe bool IsFocused(Window* window)
        => window == null
           || GLFW.GetWindowAttrib(window, WindowAttributeGetBool.Focused);

    private static float ApplyDeadZone(float v, float deadZone)
    {
        float abs = MathF.Abs(v);
        if (abs < deadZone) return 0f;
        float scaled = (abs - deadZone) / (1f - deadZone);
        return MathF.Sign(v) * scaled * scaled;
    }
}
