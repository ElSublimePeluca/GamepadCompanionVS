using System.Collections;
using System.Reflection;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Vintagestory.API.Common;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace GamepadCompanion.Input;

// QUINTA capa de estado de input: los botones del MouseState de la ventana de
// OpenTK. En el engine nadie los lee (sólo la posición, para la cámara), pero sí
// ImGui: VSImGui arma io.MouseDown en ImGuiController.CollectKeysInputs leyendo
// window.MouseState[Button1..5]. Los clicks del cursor virtual van a los GuiDialog
// y los del mundo a UpdateMouseButtonState, y ninguno pasa por acá, así que un menú
// dibujado con ImGui (el de xSkills) veía moverse el cursor pero no recibía clicks.
//
// OpenTK escribe esos bits sólo desde el callback de botones de GLFW y no los relee
// por frame (la posición sí, en NewFrame), así que lo que escribimos queda puesto
// hasta que alguien lo saque. Por eso: proyección por frame desde el `finally` del
// driver, liberación en LeaveWorld y en Dispose, y al soltar no se pisa un botón
// físico apretado, porque su propio evento de release lo va a limpiar.
//
// El campo es privado (_buttons). Es el mismo que escribe VSImGui en
// ImGuiWindow.SetMouseButton, y está en el baseline de gpclab apicheck.
internal static class NativeMouseMirror
{
    private static readonly FieldInfo? ButtonsField = typeof(MouseState).GetField(
        "_buttons", BindingFlags.Instance | BindingFlags.NonPublic);

    private static bool forcedLeft;
    private static bool forcedRight;

    public static void Commit(bool wantLeft, bool wantRight)
    {
        // Ni queremos nada ni hay nada nuestro que devolver: el estado es de OpenTK.
        if (!wantLeft && !wantRight && !forcedLeft && !forcedRight) return;
        // Sin ventana todavía (o ya sin ella) los flags quedan como estaban y la
        // devolución se reintenta el frame siguiente.
        if (Buttons() is not BitArray buttons) return;

        forcedLeft  = Project(buttons, MouseButton.Button1, EnumMouseButton.Left,
                              wantLeft, forcedLeft);
        forcedRight = Project(buttons, MouseButton.Button2, EnumMouseButton.Right,
                              wantRight, forcedRight);
    }

    public static void ClearAll() => Commit(false, false);

    // Qué escribir en un botón: true, false o nada (null). Aparte para probarlo sin
    // el juego. Sólo se suelta lo que forzamos nosotros, y sólo si el botón físico
    // está suelto.
    internal static bool? Decide(bool want, bool forced, bool physicalDown)
    {
        if (want) return true;
        if (forced && !physicalDown) return false;
        return null;
    }

    private static bool Project(BitArray buttons, MouseButton native,
                                EnumMouseButton button, bool want, bool forced)
    {
        bool physicalDown = !want && forced && ScreenInputMirror.IsMouseDown(button);
        if (Decide(want, forced, physicalDown) is bool value)
            buttons[(int)native] = value;
        return want;
    }

    private static BitArray? Buttons()
    {
        if (ButtonsField is null) return null;
        if (ScreenManager.Platform is not ClientPlatformWindows platform) return null;
        MouseState? state = platform.window?.MouseState;
        return state is null ? null : ButtonsField.GetValue(state) as BitArray;
    }
}
