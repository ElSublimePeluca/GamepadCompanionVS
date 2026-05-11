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
public sealed class TriggerMapper
{
    private const float Threshold = 0.5f;

    private readonly ICoreClientAPI capi;

    public TriggerMapper(ICoreClientAPI capi)
    {
        this.capi = capi;
    }

    public void Apply(GamepadState current, GamepadState previous)
    {
        // Solo en gameplay. Si hay un GuiDialog abierto el cursor no está
        // grabbed y meter clicks in-world arruinaría la UX (rompería bloques
        // detrás del inventario). M9 manejará el caso de cursor virtual en GUI.
        if (!capi.Input.MouseGrabbed) return;
        if (capi.World is not ClientMain client) return;

        var mouse = client.InWorldMouseState;
        if (mouse is null) return;

        bool rtNow  = current.RightTrigger  > Threshold;
        bool rtPrev = previous.RightTrigger > Threshold;
        if (rtNow != rtPrev) mouse.Left = rtNow;

        bool ltNow  = current.LeftTrigger  > Threshold;
        bool ltPrev = previous.LeftTrigger > Threshold;
        if (ltNow != ltPrev) mouse.Right = ltNow;
    }
}
