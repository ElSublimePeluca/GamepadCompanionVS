using System.Collections.Generic;
using GamepadCompanion.Toggles;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client;

namespace GamepadCompanion.Input;

// Espejo del input sintético hacia los tres estáticos de
// Vintagestory.Client.ScreenManager:
//
//     ScreenManager.KeyboardModifiers   (KeyModifiers: Ctrl/Alt/ShiftPressed)
//     ScreenManager.KeyboardKeyState    (bool[512], indexado por GlKeys)
//     ScreenManager.MouseButtonState    (bool[255], indexado por EnumMouseButton)
//
// POR QUÉ EXISTE ESTA CLASE
// ─────────────────────────
// Esos tres arrays son una CUARTA capa de estado de input, arriba de todo lo
// que el mod ya escribe. La cadena real es:
//
//   GLFW → ClientPlatformWindows.game_KeyDown / Mouse_ButtonDown
//        → ScreenManager.OnKeyDown/OnMouseDown  ← acá se escriben los 3 estáticos
//        → GuiScreenRunningGame.OnKeyDown/OnMouseDown
//        → ClientMain.OnKeyDown / OnMouseDownRaw
//        → hotkeyManager → SystemHotkeys → ClientMain.UpdateMouseButtonState
//
// El mod entra SIEMPRE por abajo: HoldKeyAction/KeyPressAction llaman a
// ClientMain.OnKeyDown, TriggerMapper llama a ClientMain.UpdateMouseButtonState,
// ToggleManager escribe ClientMain.KeyboardState. Ninguno de esos caminos
// toca los estáticos de ScreenManager, así que para cualquier mod que los
// polee el gamepad simplemente NO EXISTE.
//
// Reportado por pngwn con Hydrate or Diedrate: para beber de un bloque de agua
// HoD pide Ctrl + mantener click derecho, pero no usa un Handler de hotkey —
// polea cada 100ms leyendo `ScreenManager.KeyboardModifiers.CtrlPressed` y
// `ScreenManager.MouseButtonState[2]`. Con teclado y mouse anda; con el
// gamepad las dos mitades son false para siempre. No es específico de HoD: es
// un idiom que cualquier mod puede usar, y el arreglo tiene que ser genérico.
//
// POR QUÉ ESCRIBIMOS LOS CAMPOS A MANO Y NO LLAMAMOS A ScreenManager.OnMouseDown
// ─────────────────────────────────────────────────────────────────────────────
// Ésta es la excepción documentada a la doctrina de v1.8.0 ("rutear el input
// sintético por el pipeline real en vez de escribir estado derivado").
// ScreenManager.OnMouseDown no es un setter: escribe el estático y acto seguido
// hace CurrentScreen.OnMouseDown → GuiScreenRunningGame → ClientMain.
// OnMouseDownRaw → hotkeyManager.OnMouseButton → SystemHotkeys.
// OnSecondaryMouseButton → ClientMain.UpdateMouseButtonState, o sea exactamente
// la llamada que TriggerMapper YA hace. Rutear por ahí sería un click DOBLE:
// dos pasadas por todos los ClientSystems, dos OnBlockInteractStart por
// gatillo. Y en el release el orden es peor todavía, porque
// UpdateMouseButtonState limpia InWorldMouseState ANTES del loop de sistemas.
// Los tres campos son `public static` sobre una clase `public`, así que la
// escritura directa es legal y es la única opción correcta.
//
// Riesgo asumido, y por qué es aceptable: en todo VintagestoryLib +
// VintagestoryAPI hay CERO lectores de MouseButtonState y CERO de
// KeyboardModifiers; de KeyboardKeyState solo GuiScreenDisconnected y
// GuiScreenDownloadMods (ambos el índice 50 = Escape, por frame de render) y
// el IInputAPI del menú principal. In-game `capi.Input` es otro objeto
// (InputAPI → ClientMain.KeyboardState), así que no estamos pisando nada que
// el juego lea mientras se juega.
//
// LA REGLA: PROYECTAR, NUNCA LATCHEAR
// ───────────────────────────────────
// Los estáticos de ScreenManager no se resetean NUNCA. No hay pasada de
// reconciliación, no los limpia el cambio de foco (ScreenManager.onFocusChanged
// solo reenvía a CurrentScreen), no los limpia salir del mundo, y a diferencia
// de ClientMain.KeyboardState no se reasignan al cargar otro mundo — son
// static, viven lo que vive el proceso. Un `true` que se nos escape no se
// arregla ni reiniciando el mundo: hace falta un click físico real o cerrar el
// juego. Es la misma clase de bug que el "left trigger curse", pero sin la
// escotilla de "reiniciá el mundo" que lo hacía tolerable.
//
// Por eso el valor de cada bit se recalcula ENTERO cada frame como función pura:
//
//     bit = (lo que el mod quiere inyectar ahora) || (lo que el OS dice)
//
// - Nunca `|=` ni `= want || bit`: somos el único escritor además del OS, así
//   que un OR contra el propio array se latchea solo la primera vez.
// - Nunca un `= false` pelado: el array es edge-driven, pisar a false una
//   tecla que el usuario tiene apretada de verdad no se recupera hasta que la
//   suelte y la vuelva a apretar.
// - La verdad física sale de GLFW (IsKeyDown/IsMouseDown, acá abajo), NO de
//   ClientMain.KeyboardStateRaw: ese array lo ensuciamos nosotros vía
//   OnKeyDown y vía el heal del curse, así que como baseline mentiría.
// - Cubrimos izquierda Y derecha de cada modificador: KeyboardModifiers no es
//   por keycode, sale del bitmask de modificadores de GLFW, que se prende con
//   cualquiera de los dos lados. Un OR solo contra el modificador izquierdo
//   apagaría el Ctrl derecho de un usuario de teclado 60 veces por segundo —
//   o sea, el mod dejaría a HoD PEOR que sin mod.
//
// Corolario lindo: cuando no queremos inyectar nada, `bit = físico` es un
// no-op en cualquier estado sano (el estático ya venía del mismo evento del
// OS) y además CURA el caso patológico donde el engine se comió un KeyUp.
//
// EL SET `touched`
// ────────────────
// Para las teclas (no para los modificadores ni el mouse, que son índices
// fijos) no podemos reproyectar "todas": son 512. Guardamos qué índices
// tocamos alguna vez y los seguimos reproyectando hasta que queden igual al
// físico. Así una tecla que dejamos de mantener vuelve sola al valor del OS.
public static class ScreenInputMirror
{
    // Índices GlKeys de los modificadores. KeyboardModifiers.*Pressed no
    // distingue lados, así que el físico es el OR de los dos.
    private const int KeyShiftLeft    = 1;
    private const int KeyShiftRight   = 2;
    private const int KeyControlLeft  = 3;
    private const int KeyControlRight = 4;
    private const int KeyAltLeft      = 5;
    private const int KeyAltRight     = 6;

    // Escape. NO lo espejamos nunca: GuiScreenDisconnected y
    // GuiScreenDownloadMods poleán KeyboardKeyState[50] en cada frame de
    // render, sin debounce, y llaman StartMainMenu() apenas lo ven en true.
    // Un Escape espejado que se estirara un frame de más hace que la pantalla
    // de desconexión se cierre sola antes de que se pueda leer el motivo.
    // Ningún mod polea Escape por este canal; no perdemos nada.
    private const int KeyEscape = 50;

    // Teclas que escribimos alguna vez y que todavía hay que reproyectar.
    // Estático a propósito: HoldKeyAction se construye desde el JSON de config
    // y desde el KeyCaptureDialog, sin acceso al driver, y el espejo tiene que
    // sobrevivir a que el driver se muera (Dispose reentrante desde el menú
    // Escape). El engine mismo guarda este estado en estáticos.
    private static readonly HashSet<int> touched = new();

    // Scratch reusado por Commit para no allocar por frame.
    private static readonly HashSet<int> wanted = new();

    // Una vez que se llamó a ClearAll() dejamos de escribir. Sin esto, el
    // `finally` del driver — que sigue corriendo mientras se desarma el stack
    // de un Dispose reentrante — volvería a proyectar todo lo que ClearAll
    // acababa de limpiar.
    private static bool stopped;

    // ¿Hay algo NUESTRO vivo en los estáticos? Mientras sea false, Commit no
    // los toca en absoluto.
    //
    // Sin este corte el espejo reescribiría los tres estáticos 60 veces por
    // segundo SIEMPRE — el driver corre cada frame de render aunque no haya
    // gamepad enchufado (GamepadRenderer.OnRenderFrame llama a OnTick con el
    // state desconectado). Escribir "el valor físico" es inocuo en cualquier
    // estado sano, pero es igual un clobber: si otro mod parchea o escribe
    // KeyboardModifiers para lo suyo, se lo pisamos. Estando quietos cuando no
    // inyectamos nada, el único momento en que mandamos nosotros es el que nos
    // corresponde.
    //
    // Lo prenden también las escrituras de borde: si SetMouseEdge apretó el
    // botón pero la excepción de un handler ajeno impidió que
    // TriggerMapper.wroteRight quedara en true, `dirty` es lo único que
    // garantiza que Commit haga igual la pasada de deshacer.
    private static bool dirty;

    // Lo llama StartClientSide: cada carga de mundo instancia un ModSystem
    // nuevo, pero estos estáticos son del proceso.
    public static void Reset()
    {
        stopped = false;
        dirty   = false;
        touched.Clear();
    }

    // ───────────────────────── escrituras de borde ─────────────────────────
    // Fidelidad de orden, no corrección. Un click físico escribe
    // ScreenManager.MouseButtonState (ScreenManager.OnMouseDown) ANTES de
    // despachar hacia ClientMain, y un KeyDown físico escribe
    // KeyboardKeyState antes de bajar a ClientMain.OnKeyDown. Replicamos ese
    // orden para que un mod que polee los estáticos DENTRO de su propio
    // handler de MouseDown/KeyDown vea lo mismo que con hardware real — que es
    // exactamente la forma del bug de RKN Crafting de v1.8.1, una capa arriba.
    //
    // Si alguna de estas escrituras se pierde (excepción de un handler ajeno,
    // camino que no pasa por acá), NO pasa nada: Commit reproyecta el frame
    // siguiente. La corrección vive en Commit, no acá.

    public static void SetKeyEdge(int glKeyCode, bool down)
    {
        if (stopped) return;
        if (!IsMirrorableKey(glKeyCode)) return;
        touched.Add(glKeyCode);
        if (down) dirty = true;
        WriteKey(glKeyCode, down);

        // Solo el flanco de SUBIDA prende el flag de modificador. Apagarlo acá
        // sería un clobber: soltar un Alt mantenido borraría de paso el Ctrl
        // del toggle. Quién apaga es Commit, que ve todo lo que queremos
        // inyectar junto.
        if (!down) return;
        var mods = ScreenManager.KeyboardModifiers;
        if (mods is null) return;
        if (IsModifier(glKeyCode, KeyControlLeft, KeyControlRight)) mods.CtrlPressed  = true;
        if (IsModifier(glKeyCode, KeyShiftLeft,   KeyShiftRight))   mods.ShiftPressed = true;
        if (IsModifier(glKeyCode, KeyAltLeft,     KeyAltRight))     mods.AltPressed   = true;
    }

    public static void SetMouseEdge(EnumMouseButton button, bool down)
    {
        if (stopped) return;
        if (down) dirty = true;
        WriteMouse(button, down);
    }

    // ─────────────────────── proyección por frame ───────────────────────
    // La llama GamepadInputDriver en un `finally`, en TODOS los caminos:
    // gamepad desconectado, ventana sin foco, radial abierto, teclado virtual,
    // pausa, y el unwind de una excepción de un handler de terceros
    // (ClientEventManager.TriggerRenderStage no tiene try/catch, así que una
    // excepción ajena se lleva puesto el resto de nuestro OnTick).
    //
    // `injecting` = false significa "este frame el mod no inyecta nada": todo
    // lo nuestro se devuelve al valor físico. Es lo que hace que soltar sea
    // estructural y no una lista de casos. Y una vez devuelto, `dirty` apaga
    // el espejo hasta que volvamos a querer inyectar algo — no somos dueños de
    // estos estáticos, solo los tomamos prestados mientras hace falta.
    public static void Commit(ToggleManager toggles, TriggerMapper triggers,
                              ButtonMapper buttons, bool injecting)
    {
        if (stopped) return;

        // 1) Qué queremos inyectar este frame. wroteLeft/wroteRight son el
        //    registro autoritativo de "lo estamos manteniendo": los mismos
        //    flags con los que ReleaseInto decide si mandar el MouseUp.
        bool wantLeft  = injecting && triggers.WroteLeft;
        bool wantRight = injecting && triggers.WroteRight;

        wanted.Clear();
        if (injecting)
        {
            buttons.CollectHeldKeyCodes(wanted);
            if (toggles.ProjectedCtrl)  wanted.Add(KeyControlLeft);
            if (toggles.ProjectedShift) wanted.Add(KeyShiftLeft);
        }

        // Nada nuestro que inyectar y nada nuestro que deshacer: los estáticos
        // no son asunto nuestro este frame. El frame en que dejamos de querer
        // algo, `dirty` todavía está en true, así que la pasada de deshacer
        // (que devuelve todo al valor físico) corre completa una vez y recién
        // después nos callamos.
        bool wantAnything = wanted.Count > 0 || wantLeft || wantRight;
        if (!wantAnything && !dirty) return;
        dirty = wantAnything;

        // 2) Mouse.
        WriteMouse(EnumMouseButton.Left,  wantLeft);
        WriteMouse(EnumMouseButton.Right, wantRight);

        // 3) Teclas mantenidas + las teclas de los toggles.
        foreach (int code in wanted)
        {
            if (!IsMirrorableKey(code)) continue;
            touched.Add(code);
            WriteKey(code, true);
        }

        // Las que dejamos de querer vuelven al físico. Se sacan de `touched`
        // recién cuando el físico también las da en false, así no perdemos de
        // vista una tecla que el usuario sigue apretando a mano.
        if (touched.Count > 0)
        {
            List<int>? settled = null;
            foreach (int code in touched)
            {
                if (wanted.Contains(code)) continue;
                if (!WriteKey(code, false)) (settled ??= new()).Add(code);
            }
            if (settled is not null)
                foreach (int code in settled) touched.Remove(code);
        }

        // 4) Flags de modificadores. Se recalculan enteros porque
        //    ScreenManager.OnKeyDown/OnKeyUp reescriben los TRES en CADA
        //    evento de tecla (incluida una letra cualquiera): una escritura de
        //    una sola vez se borra al primer tecleo, y el síntoma sería "el
        //    trago se corta solo".
        WriteModifiers(wanted.Contains(KeyControlLeft) || wanted.Contains(KeyControlRight),
                       wanted.Contains(KeyShiftLeft)   || wanted.Contains(KeyShiftRight),
                       wanted.Contains(KeyAltLeft)     || wanted.Contains(KeyAltRight));
    }

    // ───────────────────────────── teardown ─────────────────────────────
    // Backstop final: deja los tres estáticos en el estado físico puro y
    // apaga el espejo. Escritura DIRECTA a propósito — en Dispose el
    // ClientMain ya tiene `disposed = true`, así que OnKeyUp es un no-op
    // silencioso y UpdateMouseButtonState caminaría ClientSystems ya
    // dispuestos. El release "de verdad" (que destraba los latches de vanilla
    // y de otros mods) va en el handler de Event.LeaveWorld, que corre antes
    // de todo eso.
    public static void ClearAll()
    {
        WriteMouse(EnumMouseButton.Left,  false);
        WriteMouse(EnumMouseButton.Right, false);
        foreach (int code in touched) WriteKey(code, false);
        touched.Clear();
        WriteModifiers(false, false, false);
        dirty   = false;
        stopped = true;
    }

    // ─────────────────────────── primitivas ───────────────────────────

    // Devuelve el valor que quedó escrito. `want || físico`, nunca a secas.
    private static bool WriteKey(int glKeyCode, bool want)
    {
        var arr = ScreenManager.KeyboardKeyState;
        if (arr is null || glKeyCode < 0 || glKeyCode >= arr.Length) return false;
        bool value = want || IsKeyDown(glKeyCode);
        arr[glKeyCode] = value;
        return value;
    }

    private static void WriteMouse(EnumMouseButton button, bool want)
    {
        var arr = ScreenManager.MouseButtonState;
        int idx = (int)button;
        // bool[255]: el array se dimensiona con Max() (=None=255) en vez de
        // Max()+1, así que el índice 255 revienta. Left/Right están holgados,
        // pero el chequeo se queda para que nadie lo rompa desde afuera.
        if (arr is null || idx < 0 || idx >= arr.Length) return;
        arr[idx] = want || IsMouseDown(button);
    }

    private static void WriteModifiers(bool wantCtrl, bool wantShift, bool wantAlt)
    {
        var mods = ScreenManager.KeyboardModifiers;
        if (mods is null) return;

        mods.CtrlPressed = wantCtrl
            || IsKeyDown(KeyControlLeft) || IsKeyDown(KeyControlRight);
        mods.ShiftPressed = wantShift
            || IsKeyDown(KeyShiftLeft)   || IsKeyDown(KeyShiftRight);
        mods.AltPressed = wantAlt
            || IsKeyDown(KeyAltLeft)     || IsKeyDown(KeyAltRight);
    }

    private static bool IsModifier(int code, int left, int right)
        => code == left || code == right;

    // Espejables: keycodes reales de teclado. Fuera quedan los pseudo-códigos
    // de mouse (240+, que ScreenManager nunca pone en KeyboardKeyState: el
    // mirror de mouse del engine va a ClientMain.KeyboardState, no acá) y
    // Escape.
    private static bool IsMirrorableKey(int glKeyCode)
        => glKeyCode > 0 && glKeyCode < KeyConverter.GlKeysToNew.Length
           && glKeyCode != KeyEscape;

    // ¿El OS reporta esta tecla apretada? Único punto de verdad física del
    // mod: lo usa el espejo como baseline y TriggerMapper para decidir si una
    // tecla latcheada en KeyboardState es un fantasma. KeyConverter.GlKeysToNew
    // traduce GlKeys → el enum Keys de GLFW (-1 si no hay equivalente). Sin
    // ventana (headless / contexto no current) devolvemos false: el espejo
    // queda en "solo lo que el mod quiere", que es el lado seguro.
    public static unsafe bool IsKeyDown(int glKeyCode)
    {
        if (glKeyCode < 0 || glKeyCode >= KeyConverter.GlKeysToNew.Length)
            return false;
        int glfwKey = KeyConverter.GlKeysToNew[glKeyCode];
        if (glfwKey < 0) return false;

        var window = GLFW.GetCurrentContext();
        if (window == null) return false;
        return GLFW.GetKey(window, (Keys)glfwKey) == InputAction.Press;
    }

    // OJO: los enums NO coinciden. EnumMouseButton es Left=0/Middle=1/Right=2;
    // el MouseButton de OpenTK es Left=Button1=0/Right=Button2=1/Middle=
    // Button3=2. Es la misma traducción que hace
    // Vintagestory.API.Common.MouseButtonConverter en el sentido contrario.
    public static unsafe bool IsMouseDown(EnumMouseButton button)
    {
        MouseButton glfwButton;
        switch (button)
        {
            case EnumMouseButton.Left:   glfwButton = MouseButton.Button1; break;
            case EnumMouseButton.Right:  glfwButton = MouseButton.Button2; break;
            case EnumMouseButton.Middle: glfwButton = MouseButton.Button3; break;
            default: return false;
        }

        var window = GLFW.GetCurrentContext();
        if (window == null) return false;
        return GLFW.GetMouseButton(window, glfwButton) == InputAction.Press;
    }

    // Para el .gptrace. Solo lo que un mod que usa este idiom lee de verdad:
    // los flags de modificador y los dos botones de mouse. Loguear
    // KeyboardKeyState[3] sería engañoso — HoD lee el flag, no el array.
    public static string Describe()
    {
        var mods = ScreenManager.KeyboardModifiers;
        var mb   = ScreenManager.MouseButtonState;
        char c = mods?.CtrlPressed  == true ? '1' : '0';
        char s = mods?.ShiftPressed == true ? '1' : '0';
        char a = mods?.AltPressed   == true ? '1' : '0';
        char l = mb is not null && mb.Length > (int)EnumMouseButton.Left
                 && mb[(int)EnumMouseButton.Left] ? 'L' : '-';
        char r = mb is not null && mb.Length > (int)EnumMouseButton.Right
                 && mb[(int)EnumMouseButton.Right] ? 'R' : '-';
        return $"c{c}/s{s}/a{a}/mb{l}{r}/k{touched.Count}";
    }
}
