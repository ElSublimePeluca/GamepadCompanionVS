using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;

namespace GamepadCompanion.Input;

// RT → click izquierdo (atacar/romper), LT → click derecho (interactuar/colocar),
// vía ClientMain.InWorldMouseState.Left / Right con edge-trigger.
// El layout es invertido respecto al "estándar" de gamepad porque calza mejor
// con el reflejo del usuario para esta mecánica.
//
// Probado y descartado: EntityControls.LeftMouseDown / RightMouseDown — esos
// solo disparan la animación del personaje (swing del brazo) pero el sistema
// de interacciones in-world no los lee. La fuente autoritativa es
// SystemMouseInWorldInteractions, que lee de ClientMain.InWorldMouseState
// (campo público, MouseButtonState con bool Left/Middle/Right).
//
// A diferencia de los flags de movimiento (que el engine resetea cada frame y
// se reescriben mientras el botón está apretado), InWorldMouseState es
// persistente: lo escribe OnMouseDownRaw/OnMouseUpRaw del engine en press y
// release. Por eso usamos edge-trigger: writing true on press, false on release.
//
// Trackeamos wroteLeft/wroteRight para poder soltar los flags si perdemos el
// "permiso" de inyectar in-world (GuiDialog modal se abre mid-press, radial,
// teclado virtual, foco perdido). Si no, ej. LT abre un cofre → MouseGrabbed
// pasa a false pero el engine sigue leyendo InWorldMouseState vía
// mouseWorldInteractAnyway, y como SystemMouseInWorldInteractions polea el
// estado continuamente (no edge-triggered), re-disparaba OnBlockInteractStart
// cada BuildRepeatDelay (~0.25s) → el cofre toggleaba open/close en loop.
public sealed class TriggerMapper
{
    private const float Threshold = 0.5f;

    private readonly ICoreClientAPI capi;
    private bool wroteLeft;
    private bool wroteRight;

    public TriggerMapper(ICoreClientAPI capi)
    {
        this.capi = capi;
    }

    public void Apply(GamepadState current, GamepadState previous)
    {
        if (capi.World is not ClientMain client) return;
        var mouse = client.InWorldMouseState;
        if (mouse is null) return;

        // Sin grab no inyectamos clicks in-world (rompería bloques detrás del
        // inventario). Soltamos cualquier flag pegado del press anterior.
        if (!capi.Input.MouseGrabbed)
        {
            ReleaseInto(mouse);
            return;
        }

        bool rtNow  = current.RightTrigger  > Threshold;
        bool rtPrev = previous.RightTrigger > Threshold;
        if (rtNow != rtPrev)
        {
            mouse.Left = rtNow;
            wroteLeft = rtNow;
        }

        bool ltNow  = current.LeftTrigger  > Threshold;
        bool ltPrev = previous.LeftTrigger > Threshold;
        if (ltNow != ltPrev)
        {
            mouse.Right = ltNow;
            wroteRight = ltNow;
        }
    }

    // Para que el driver lo llame en branches donde Apply se saltea (radial,
    // teclado virtual, modo cursor virtual). Esas dialogs son HUD-type y no
    // ungrabean el mouse, así que el chequeo interno de MouseGrabbed en Apply
    // no las cubriría — necesitamos un release explícito.
    public void Release()
    {
        if (capi.World is not ClientMain client) return;
        var mouse = client.InWorldMouseState;
        if (mouse is null) return;
        ReleaseInto(mouse);
    }

    private void ReleaseInto(MouseButtonState mouse)
    {
        if (wroteLeft)  { mouse.Left  = false; wroteLeft  = false; }
        if (wroteRight) { mouse.Right = false; wroteRight = false; }
    }
}
