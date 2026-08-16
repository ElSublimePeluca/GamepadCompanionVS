using System;
using System.Collections.Generic;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Vintagestory.API.Common;

namespace GamepadCompanion.Input;

// Lee el joystick directamente vía GLFW joystick raw API — sin pasar por
// el sistema de "gamepad mapping" de GLFW (que rechazó el mapping del Cyclone 2
// silenciosamente, sin forma de diagnosticar). Asume por default layout xpad:
// botones 0..10 en orden A,B,X,Y,LB,RB,Back,Start,Guide,L3,R3,
// axes 0..5 en orden LX,LY,LT,RX,RY,RT, dpad como axes 6/7 o como hat 0.
//
// Detecta cinco layouts distintos vía firma:
//   Xpad        — Linux xpad clásico. Default fallback.
//                 axes: LX,LY,LT,RX,RY,RT (0..5)
//                 buttons: A,B,X,Y,LB,RB,Back,Start,Guide,L3,R3 (0..10)
//   Ds4Amazon   — PS4-DInput "estándar Sony", visto en el control
//                 "Wired Controller" (Amazon B0CZ3WKF58, Ubsvaky LBE-PS4).
//                 Firma: a3 Y a4 ambos ≈ -1 al primer poll (los dos triggers
//                 signed en posiciones 3/4 en reposo). Hay que pedir AMBOS,
//                 no uno solo, porque WinXInput también reporta a4 signed
//                 (LT) — chequeando los dos a la vez se evita el falso match.
//                 axes: LX,LY,RX,LT,RT,RY (0..5)
//                 face buttons: Square,Cross,Circle,Triangle (raw 0..3)
//   GameSirPs4  — GameSir Cyclone 2 en modo PS4 (probablemente otros del
//                 mismo fabricante). Firma: nombre contiene "Chicken Run"
//                 (manufacturer Guangzhou Chicken Run Network Technology
//                 = casa matriz de GameSir) o "GameSir". No se puede
//                 detectar por axes solos porque comparte la firma de
//                 triggers signed a2/a5 con xpad clásico.
//                 axes: LX,LY,LT,RX,RY,RT (0..5) — igual a xpad
//                 face buttons: raw 0=A, 1=B, 2=Y, 3=X (X/Y swapped
//                 contra xpad en raw 2/3; A y B sí siguen xpad)
//   WinXInput   — Windows XInput vía GLFW. Reportado por springrain con
//                 8BitDo Ultimate 2C Wireless en modo Xbox, y por pngwn con
//                 "Controller (XBOX 360 for Windows)". GLFW en Windows
//                 normaliza el orden XInput a LX,LY,RX,RY,LT,RT (distinto
//                 de Linux xpad). Firma: a4 < -0.9 al primer poll (LT
//                 signed en reposo) pero a3 NO está signed (=0, RY en
//                 reposo) — eso distingue contra Ds4Amazon (a3 ∧ a4).
//                 axes: LX,LY,RX,RY,LT,RT (0..5)
//                 buttons: A,B,X,Y,LB,RB,Back,Start,L3,R3 (0..9) — sin
//                 Guide (XInput no expone la tecla Guide)
//   XboxBtHid   — Xbox One/Series por Bluetooth en Linux, manejado por el
//                 driver HID genérico del kernel (no xpad, que es solo USB).
//                 Reportado por GingeeMaestro con un Xbox One en Steam Deck.
//                 El descriptor HID de Xbox en modo BT deja huecos en los
//                 usages: raw 2 (BTN_C) y raw 5 (BTN_Z) no existen físicamente,
//                 y raw 8/9 (BTN_TL2/TR2) son los triggers en versión digital
//                 — los ignoramos para no duplicar los trigger axes.
//                 Firma: un trigger signed en a4, a3 no signed, pero
//                 btnCount >= 15, contra los 14 que reporta GLFW en Windows
//                 XInput (10 botones + 4 del hat). El device del log traía
//                 19 = 15 reales + 4 del hat.
//                 axes: LX,LY,RX,RY,RT,LT (0..5) — mismos índices que
//                 WinXInput pero los triggers vienen intercambiados
//                 (RT físico=a4, LT físico=a5), ver el switch en PollRaw.
//                 buttons: A,B,_,X,Y,_,LB,RB,LT_btn,RT_btn,Back,Start,Guide,
//                 L3,R3 (0..14). D-pad vía hat 0 (solo 6 axes).
//   XdGamepad   — SHANWAN PS3-DInput "X-D GamePad" (vendor 0x2563). Modo
//                 secundario del control de pngwn (modelo desconocido, con
//                 lightbar verde/rojo/azul indicando modo): apretando Home
//                 5s switchea entre "Controller (XBOX 360 for Windows)" y
//                 "X-D GamePad". Solo 4 axes (sin trigger axes) y triggers
//                 expuestos como botones digitales b6/b7. Detección por
//                 nombre porque 4 axes no es una firma única.
//                 axes: LX,LY,RX,RY (0..3)
//                 buttons: A,B,X,Y,LB,RB,LT_btn,RT_btn,Back,Start,L3,R3
//                 (0..11), Guide ausente. Triggers solo digitales (0 o 1).
// Los layouts PS4 ignoran raw 6 y 7 (L2btn/R2btn) para evitar que apretar
// los triggers dispare también ghost-button actions.
public sealed class GlfwGamepadProvider : IGamepadProvider
{
    private const int MaxJoysticks = 16;
    private const int DiagnosticIntervalPolls = 60;
    private const int MinExpectedButtons = 11;
    private const int MinExpectedAxes = 6;
    private const float DpadThreshold = 0.5f;
    private const float Ds4LayoutThreshold = -0.9f;
    private const float PinnedAxisThreshold = 0.99f;

    private const int XboxBtHidMinButtons = 15;

    private enum AxisLayout
    {
        Unknown, Xpad, Ds4Amazon, GameSirPs4, WinXInput, XdGamepad, XboxBtHid,
    }

    // bit-index (orden GamepadButton) → raw button index. 11 entradas:
    // A,B,X,Y,LB,RB,Back,Start,Guide,L3,R3. -1 = botón ausente en este layout.
    private static readonly int[] Ds4AmazonButtonMap = {
        1,  2,  0,  3,  4,  5,  8,  9, 10, 11, 12,
    };
    private static readonly int[] GameSirPs4ButtonMap = {
        0,  1,  3,  2,  4,  5,  8,  9, 10, 11, 12,
    };
    // GLFW en Windows XInput omite la tecla Guide (XInput la reserva al
    // sistema), entonces L3/R3 quedan corridos a 8/9 en vez de 9/10.
    private static readonly int[] WinXInputButtonMap = {
        0,  1,  2,  3,  4,  5,  6,  7, -1,  8,  9,
    };
    // SHANWAN PS3 "X-D GamePad": order Xbox-style (A=0 south, B=1 east), pero
    // triggers expuestos como botones digitales en raw 6/7 (leídos aparte,
    // no van al face-button map). Guide ausente en modo DInput.
    private static readonly int[] XdGamepadButtonMap = {
        0,  1,  2,  3,  4,  5,  8,  9, -1, 10, 11,
    };
    // Xbox por Bluetooth vía hid-generic: huecos en raw 2 y 5, y los triggers
    // digitales en raw 8/9 quedan fuera del map (ya vienen por a4/a5).
    private static readonly int[] XboxBtHidButtonMap = {
        0,  1,  3,  4,  6,  7, 10, 11, 12, 13, 14,
    };

    private readonly ILogger logger;
    private readonly GLFWCallbacks.ErrorCallback errorCallback;
    private bool disposed;
    private int? joystickId;
    private int pollsWithoutGamepad;
    private AxisLayout layout;

    // Detección del rango de los triggers. Algunos dispositivos (xpad clásico)
    // reportan los triggers en [-1, +1] con reposo en -1. Otros (varios gamepads
    // chinos modernos como GameSir Cyclone 2) reportan en [0, 1] con reposo en 0.
    // Si normalizamos asumiendo el primer rango sobre uno del segundo, los triggers
    // quedan en 0.5 en reposo → con threshold 0.5 cualquier microruido dispara
    // transiciones false que pisan los clicks del mouse físico.
    // Detectamos el rango "sticky": si alguna vez vemos un valor negativo en el
    // trigger, asumimos [-1, +1]; sino, [0, 1]. La detección ocurre naturalmente
    // dentro de los primeros polls porque los dispositivos [-1, +1] reportan -1
    // en reposo.
    private bool ltRangeSigned;
    private bool rtRangeSigned;

    // Guardia post-conexión contra devices fantasma (ver AxesLookPinned). El
    // chequeo de selección mira los ejes en el instante de elegir, y algunos
    // HID reportan todo en 0 hasta que llega su primer reporte real: ese
    // device pasaría el filtro y recién después se clavaría en -1. Si los
    // sticks están al extremo en TODOS los polls del primer par de segundos,
    // lo soltamos y volvemos a escanear. No aplica a la selección manual: si
    // el usuario eligió ese device, es el que quiere.
    private const int PhantomGuardPolls = 120;
    private bool manualSelection;
    private int pollsSinceConnect;
    private int pinnedPollsSinceConnect;

    public bool IsConnected => joystickId.HasValue;
    public string? DeviceName { get; private set; }
    public string? PreferredDeviceName { get; set; }

    public GlfwGamepadProvider(ILogger logger)
    {
        this.logger = logger;
        errorCallback = OnGlfwError;
        GLFW.SetErrorCallback(errorCallback);
    }

    public GamepadState Poll()
    {
        if (disposed) return GamepadState.Disconnected;

        if (joystickId is int currentId && !GLFW.JoystickPresent(currentId))
            ReleaseGamepad("disconnected");

        if (!joystickId.HasValue)
        {
            // El escaneo corre en cada poll mientras no hay gamepad, así que
            // solo logueamos una vez por segundo para no inundar el log.
            bool verbose = pollsWithoutGamepad % DiagnosticIntervalPolls == 0;
            TryFindGamepad(verbose);
            if (!joystickId.HasValue)
            {
                if (verbose) LogDiagnosticScan("scan");
                pollsWithoutGamepad++;
                return GamepadState.Disconnected;
            }
            pollsWithoutGamepad = 0;
        }

        if (joystickId is not int jid) return GamepadState.Disconnected;

        return PollRaw(jid);
    }

    private unsafe void TryFindGamepad(bool verbose)
    {
        // 1. Override manual del usuario (.gpdevice). Gana sobre cualquier
        //    heurística: si alguien lo fijó a mano es justamente porque la
        //    autodetección se equivocó, así que ni filtramos ni validamos.
        string? pref = PreferredDeviceName;
        if (!string.IsNullOrWhiteSpace(pref))
        {
            for (int jid = 0; jid < MaxJoysticks; jid++)
            {
                if (!GLFW.JoystickPresent(jid)) continue;
                string name = GLFW.GetJoystickName(jid) ?? "<unknown>";
                if (name.IndexOf(pref, StringComparison.OrdinalIgnoreCase) < 0) continue;
                AcceptGamepad(jid, name, $"preferred device \"{pref}\"", manual: true);
                return;
            }
            // El device preferido no está enchufado. Caemos a autodetección
            // en vez de dejar al usuario sin control, pero lo decimos.
            if (verbose)
                logger.Notification(
                    $"GamepadCompanion: preferred device \"{pref}\" not present, " +
                    $"falling back to autodetect");
        }

        // 2. Autodetección: primer joystick con forma de gamepad que además
        //    no parezca un device fantasma.
        for (int jid = 0; jid < MaxJoysticks; jid++)
        {
            if (!GLFW.JoystickPresent(jid)) continue;

            string name = GLFW.GetJoystickName(jid) ?? "<unknown>";
            _ = GLFW.GetJoystickButtonsRaw(jid, out int btnCount);
            float* axsPtr = GLFW.GetJoystickAxesRaw(jid, out int axsCount);

            string? reject = RejectReason(name, btnCount, axsCount, axsPtr);
            if (reject is not null)
            {
                // Rate-limited igual que el scan: esto corre en cada poll.
                if (verbose)
                    logger.Notification(
                        $"GamepadCompanion: skipping jid={jid} \"{name}\" — {reject}");
                continue;
            }

            AcceptGamepad(jid, name, "autodetect", manual: false);
            return;
        }
    }

    private unsafe void AcceptGamepad(int jid, string name, string how, bool manual)
    {
        joystickId = jid;
        DeviceName = name;
        manualSelection = manual;
        pollsSinceConnect = 0;
        pinnedPollsSinceConnect = 0;
        _ = GLFW.GetJoystickButtonsRaw(jid, out int btnCount);
        _ = GLFW.GetJoystickAxesRaw(jid, out int axsCount);
        logger.Notification(
            $"GamepadCompanion: gamepad connected on jid={jid}: {DeviceName} " +
            $"(buttons={btnCount}, axes={axsCount}) [{how}]");
        // Dejamos constancia del resto de los joysticks presentes: si elegimos
        // mal, el log del usuario ya trae las alternativas y el .gpdevice que
        // hay que tipear, sin pedirle otra corrida.
        LogDiagnosticScan("candidates at connect");
    }

    // null = el device es aceptable. Si no, el motivo del descarte.
    private static unsafe string? RejectReason(string name, int btnCount, int axsCount,
                                               float* axsPtr)
    {
        if (IsKnownNotGamepad(name)) return "known non-gamepad device";

        bool standardSig  = btnCount >= MinExpectedButtons && axsCount >= MinExpectedAxes;
        bool xdGamepadSig = IsXdGamepadName(name) && btnCount >= 12 && axsCount >= 4;
        if (!standardSig && !xdGamepadSig)
            return $"not gamepad-shaped (buttons={btnCount}, axes={axsCount})";

        if (axsPtr != null && AxesLookPinned(axsPtr, axsCount))
            return $"sticks pinned at the extreme (a0={axsPtr[0]:+0.00;-0.00;0.00} " +
                   $"a1={axsPtr[1]:+0.00;-0.00;0.00}), looks like a phantom device";

        return null;
    }

    // Un gamepad real en reposo tiene los sticks cerca de 0 — los triggers sí
    // pueden estar en -1 (varios layouts lo hacen), pero LX/LY no. Un device
    // HID que no es un gamepad suele reportar TODOS sus ejes clavados en -1
    // para siempre, y eso in-game se traduce en caminar en diagonal mientras
    // la cámara gira sola sin parar.
    // Caso real: Lyn_ en SteamOS tenía jid=0 = "ASRock LED Controller" (la
    // controladora RGB de la placa madre: 12 botones, 10 ejes, todos en -1.00
    // durante los 1770 frames del gptrace). Le ganaba la selección al Steam
    // Controller y el personaje giraba en círculos mirando arriba a la
    // izquierda sin tocar nada.
    // Rechazar y seguir escaneando es seguro incluso ante un falso positivo:
    // la selección se rehace en cada poll mientras no hay gamepad, así que un
    // control real que arrancó con el stick a fondo entra apenas lo sueltan.
    private static unsafe bool AxesLookPinned(float* axsPtr, int axsCount) =>
        axsCount >= 2
        && Math.Abs(axsPtr[0]) >= PinnedAxisThreshold
        && Math.Abs(axsPtr[1]) >= PinnedAxisThreshold;

    // Joysticks-shaped que no son gamepads (teclados/mouse con botones programables
    // que el kernel también expone como /dev/input/js*). Crece con cada caso real.
    private static bool IsKnownNotGamepad(string name) =>
        name.Contains("Keychron",       StringComparison.OrdinalIgnoreCase) ||
        name.Contains("LED Controller", StringComparison.OrdinalIgnoreCase);

    // SHANWAN PS3 DInput modes: 4 axes + triggers como botones. No pasan el
    // filtro estándar de MinExpectedAxes=6, hay que matchearlos por nombre.
    private static bool IsXdGamepadName(string name) =>
        name.Contains("X-D GamePad", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("SHANWAN",     StringComparison.OrdinalIgnoreCase);

    private unsafe GamepadState PollRaw(int jid)
    {
        JoystickInputAction* btnPtr = GLFW.GetJoystickButtonsRaw(jid, out int btnCount);
        float* axsPtr = GLFW.GetJoystickAxesRaw(jid, out int axsCount);

        if (btnPtr == null || axsPtr == null)
        {
            ReleaseGamepad("null pointer from GLFW");
            return GamepadState.Disconnected;
        }

        if (!manualSelection && pollsSinceConnect < PhantomGuardPolls)
        {
            pollsSinceConnect++;
            if (AxesLookPinned(axsPtr, axsCount)) pinnedPollsSinceConnect++;
            if (pollsSinceConnect >= PhantomGuardPolls
                && pinnedPollsSinceConnect == pollsSinceConnect)
            {
                logger.Notification(
                    $"GamepadCompanion: \"{DeviceName}\" held its sticks at the extreme " +
                    $"for {PhantomGuardPolls} polls straight ({FormatAxes(axsPtr, axsCount)}) " +
                    $"— treating it as a phantom device and rescanning. " +
                    $"Use .gpdevice if this is really your gamepad.");
                ReleaseGamepad("sticks pinned since connect");
                return GamepadState.Disconnected;
            }
        }

        DetectLayout(axsPtr, axsCount, btnCount, DeviceName ?? string.Empty);

        ushort bits = ReadFaceButtons(btnPtr, btnCount);

        // D-pad: axes 6 (X) y 7 (Y) en xpad moderno, o hat 0 como fallback.
        if (axsCount >= 8)
        {
            float dpadX = axsPtr[6];
            float dpadY = axsPtr[7];
            if (dpadY < -DpadThreshold) bits |= (ushort)(1 << (int)GamepadButton.DPadUp);
            if (dpadY >  DpadThreshold) bits |= (ushort)(1 << (int)GamepadButton.DPadDown);
            if (dpadX < -DpadThreshold) bits |= (ushort)(1 << (int)GamepadButton.DPadLeft);
            if (dpadX >  DpadThreshold) bits |= (ushort)(1 << (int)GamepadButton.DPadRight);
        }
        else
        {
            JoystickHats* hatPtr = GLFW.GetJoystickHatsRaw(jid, out int hatCount);
            if (hatPtr != null && hatCount > 0)
            {
                JoystickHats hat = hatPtr[0];
                if ((hat & JoystickHats.Up)    != 0) bits |= (ushort)(1 << (int)GamepadButton.DPadUp);
                if ((hat & JoystickHats.Right) != 0) bits |= (ushort)(1 << (int)GamepadButton.DPadRight);
                if ((hat & JoystickHats.Down)  != 0) bits |= (ushort)(1 << (int)GamepadButton.DPadDown);
                if ((hat & JoystickHats.Left)  != 0) bits |= (ushort)(1 << (int)GamepadButton.DPadLeft);
            }
        }

        // Sticks: Y se invierte para que arriba sea +1 (xpad reporta arriba=-1).
        // Índices según layout detectado.
        int idxLT, idxRT, idxRX, idxRY;
        switch (layout)
        {
            case AxisLayout.Ds4Amazon:
                idxLT = 3; idxRT = 4; idxRX = 2; idxRY = 5;
                break;
            case AxisLayout.GameSirPs4:
                idxLT = 2; idxRT = 5; idxRX = 3; idxRY = 4;
                break;
            case AxisLayout.WinXInput:
                idxLT = 4; idxRT = 5; idxRX = 2; idxRY = 3;
                break;
            case AxisLayout.XboxBtHid:
                // Xbox por BT reporta los triggers al revés que WinXInput: el
                // trigger físico DERECHO cae en a4 y el IZQUIERDO en a5. En
                // v1.5.0 se asumió el orden de WinXInput (LT=a4, RT=a5) y salían
                // intercambiados in-game (RT interactuaba, LT rompía). Confirmado
                // por GingeeMaestro comparando contra el layout USB (xpad).
                idxLT = 5; idxRT = 4; idxRX = 2; idxRY = 3;
                break;
            case AxisLayout.XdGamepad:
                // Sin trigger axes — se leen de los botones b6/b7 abajo.
                idxLT = -1; idxRT = -1; idxRX = 2; idxRY = 3;
                break;
            default: // Xpad
                idxLT = 2; idxRT = 5; idxRX = 3; idxRY = 4;
                break;
        }

        float leftX  =  axsPtr[0];
        float leftY  = -axsPtr[1];
        float rightX =  axsPtr[idxRX];
        float rightY = -axsPtr[idxRY];

        float leftT, rightT;
        if (layout == AxisLayout.XdGamepad)
        {
            // Triggers digitales: lleno o vacío, sin curva analógica.
            leftT  = (6 < btnCount && btnPtr[6] != JoystickInputAction.Release) ? 1f : 0f;
            rightT = (7 < btnCount && btnPtr[7] != JoystickInputAction.Release) ? 1f : 0f;
        }
        else
        {
            if (axsPtr[idxLT] < -0.05f) ltRangeSigned = true;
            if (axsPtr[idxRT] < -0.05f) rtRangeSigned = true;
            leftT  = NormalizeTrigger(axsPtr[idxLT], ltRangeSigned);
            rightT = NormalizeTrigger(axsPtr[idxRT], rtRangeSigned);
        }

        return new GamepadState(bits, leftX, leftY, rightX, rightY, leftT, rightT);
    }

    // Heurística sticky: una vez detectado, no vuelve a Unknown hasta desconexión.
    // Orden de chequeo (priorizamos signals más específicos):
    //   1. Nombre matchea X-D GamePad / SHANWAN → XdGamepad. Necesario por
    //      nombre porque tiene solo 4 axes (no hay firma de ejes que sirva).
    //   2. Nombre matchea GameSir/Chicken Run → GameSirPs4. Necesario por
    //      nombre porque su firma de ejes (signed triggers a2/a5) coincide
    //      con xpad clásico de Linux.
    //   3. a3 Y a4 ambos < -0.9 al primer poll → Ds4Amazon. Los dos triggers
    //      están signed en posiciones 3/4 y en reposo reportan -1.
    //   4. a4 < -0.9 pero a3 NO < -0.9 al primer poll → WinXInput. Solo LT
    //      está signed a -1 (RY=a3 está en 0 al reposo). El check de a3 alto
    //      excluye Ds4Amazon (donde a3 también está en -1).
    //   5. Default → Xpad. Logueamos los ejes para diagnóstico de layouts
    //      futuros que caigan acá sin firma conocida.
    private unsafe void DetectLayout(float* axsPtr, int axsCount, int btnCount,
                                     string deviceName)
    {
        if (layout != AxisLayout.Unknown) return;

        string axesDump = FormatAxes(axsPtr, axsCount);

        if (IsXdGamepadName(deviceName))
        {
            layout = AxisLayout.XdGamepad;
            logger.Notification(
                $"GamepadCompanion: detected XdGamepad layout by name " +
                $"(btnCount={btnCount}, axes={axesDump})");
            return;
        }

        if (deviceName.Contains("Chicken Run", StringComparison.OrdinalIgnoreCase) ||
            deviceName.Contains("GameSir",     StringComparison.OrdinalIgnoreCase))
        {
            layout = AxisLayout.GameSirPs4;
            ltRangeSigned = true;
            rtRangeSigned = true;
            logger.Notification(
                $"GamepadCompanion: detected GameSirPs4 layout by name " +
                $"(btnCount={btnCount}, axes={axesDump})");
            return;
        }

        if (axsCount > 4
            && axsPtr[3] < Ds4LayoutThreshold
            && axsPtr[4] < Ds4LayoutThreshold)
        {
            layout = AxisLayout.Ds4Amazon;
            ltRangeSigned = true;
            rtRangeSigned = true;
            logger.Notification(
                $"GamepadCompanion: detected Ds4Amazon layout by signed triggers " +
                $"(btnCount={btnCount}, axes={axesDump})");
            return;
        }

        if (axsCount > 5
            && axsPtr[4] < Ds4LayoutThreshold
            && axsPtr[3] >= Ds4LayoutThreshold
            && btnCount >= XboxBtHidMinButtons)
        {
            layout = AxisLayout.XboxBtHid;
            ltRangeSigned = true;
            rtRangeSigned = true;
            logger.Notification(
                $"GamepadCompanion: detected XboxBtHid layout by signed trigger at a4 " +
                $"+ btnCount>={XboxBtHidMinButtons} " +
                $"(btnCount={btnCount}, axes={axesDump})");
            return;
        }

        if (axsCount > 5
            && axsPtr[4] < Ds4LayoutThreshold
            && axsPtr[3] >= Ds4LayoutThreshold)
        {
            layout = AxisLayout.WinXInput;
            ltRangeSigned = true;
            rtRangeSigned = true;
            logger.Notification(
                $"GamepadCompanion: detected WinXInput layout by signed LT at a4 " +
                $"(btnCount={btnCount}, axes={axesDump})");
            return;
        }

        layout = AxisLayout.Xpad;
        logger.Notification(
            $"GamepadCompanion: detected Xpad layout (default fallback) " +
            $"(btnCount={btnCount}, axes={axesDump})");
    }

    private static unsafe string FormatAxes(float* axsPtr, int axsCount)
    {
        var sb = new System.Text.StringBuilder(axsCount * 8 + 2);
        sb.Append('[');
        for (int i = 0; i < axsCount; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture,
                            "a{0}={1:+0.00;-0.00;0.00}", i, axsPtr[i]);
        }
        sb.Append(']');
        return sb.ToString();
    }

    private unsafe ushort ReadFaceButtons(JoystickInputAction* btnPtr, int btnCount)
    {
        ushort bits = 0;
        int[]? map = layout switch
        {
            AxisLayout.Ds4Amazon  => Ds4AmazonButtonMap,
            AxisLayout.GameSirPs4 => GameSirPs4ButtonMap,
            AxisLayout.WinXInput  => WinXInputButtonMap,
            AxisLayout.XdGamepad  => XdGamepadButtonMap,
            AxisLayout.XboxBtHid  => XboxBtHidButtonMap,
            _                     => null,
        };

        if (map is not null)
        {
            for (int bit = 0; bit < map.Length; bit++)
            {
                int raw = map[bit];
                if (raw >= 0 && raw < btnCount
                    && btnPtr[raw] != JoystickInputAction.Release)
                    bits |= (ushort)(1 << bit);
            }
        }
        else
        {
            int btns = Math.Min(btnCount, 11);
            for (int i = 0; i < btns; i++)
                if (btnPtr[i] != JoystickInputAction.Release) bits |= (ushort)(1 << i);
        }
        return bits;
    }

    private static float NormalizeTrigger(float raw, bool signedRange)
    {
        // signedRange = false: el dispositivo reporta [0, 1] con reposo en 0.
        //                      Pasamos directo (clamped por seguridad).
        // signedRange = true:  reporta [-1, +1] con reposo en -1.
        //                      Reescalamos a [0, 1] con (raw+1)/2.
        if (signedRange) return (raw + 1f) * 0.5f;
        return raw < 0f ? 0f : (raw > 1f ? 1f : raw);
    }

    public unsafe float[] GetRawAxesSnapshot()
    {
        if (joystickId is not int jid || !GLFW.JoystickPresent(jid))
            return Array.Empty<float>();
        float* p = GLFW.GetJoystickAxesRaw(jid, out int count);
        if (p == null) return Array.Empty<float>();
        var result = new float[count];
        for (int i = 0; i < count; i++) result[i] = p[i];
        return result;
    }

    public unsafe byte[] GetRawButtonsSnapshot()
    {
        if (joystickId is not int jid || !GLFW.JoystickPresent(jid))
            return Array.Empty<byte>();
        JoystickInputAction* p = GLFW.GetJoystickButtonsRaw(jid, out int count);
        if (p == null) return Array.Empty<byte>();
        var result = new byte[count];
        for (int i = 0; i < count; i++)
            result[i] = (byte)(p[i] != JoystickInputAction.Release ? 1 : 0);
        return result;
    }

    public unsafe IReadOnlyList<GamepadDeviceInfo> ScanDevices()
    {
        var found = new List<GamepadDeviceInfo>();
        for (int jid = 0; jid < MaxJoysticks; jid++)
        {
            if (!GLFW.JoystickPresent(jid)) continue;
            string name = GLFW.GetJoystickName(jid) ?? "<unknown>";
            _ = GLFW.GetJoystickButtonsRaw(jid, out int btnCount);
            float* axsPtr = GLFW.GetJoystickAxesRaw(jid, out int axsCount);
            _ = GLFW.GetJoystickHatsRaw(jid, out int hatCount);
            string? reject = RejectReason(name, btnCount, axsCount, axsPtr);
            found.Add(new GamepadDeviceInfo(jid, name, btnCount, axsCount, hatCount,
                                            reject is null, reject, jid == joystickId));
        }
        return found;
    }

    public bool SelectDevice(int jid)
    {
        if (jid < 0 || jid >= MaxJoysticks || !GLFW.JoystickPresent(jid)) return false;
        string name = GLFW.GetJoystickName(jid) ?? "<unknown>";
        if (joystickId.HasValue) ReleaseGamepad("switching device");
        PreferredDeviceName = name;
        AcceptGamepad(jid, name, "manual selection", manual: true);
        return true;
    }

    public void ResetSelection()
    {
        PreferredDeviceName = null;
        if (joystickId.HasValue) ReleaseGamepad("selection reset");
    }

    private void ReleaseGamepad(string reason)
    {
        if (joystickId is int jid)
            logger.Notification($"GamepadCompanion: gamepad jid={jid} released ({reason})");
        joystickId = null;
        DeviceName = null;
        pollsWithoutGamepad = 0;
        ltRangeSigned = false;
        rtRangeSigned = false;
        layout = AxisLayout.Unknown;
        manualSelection = false;
        pollsSinceConnect = 0;
        pinnedPollsSinceConnect = 0;
    }

    private unsafe void LogDiagnosticScan(string label)
    {
        bool foundAny = false;
        for (int jid = 0; jid < MaxJoysticks; jid++)
        {
            if (!GLFW.JoystickPresent(jid)) continue;
            foundAny = true;
            string name = GLFW.GetJoystickName(jid) ?? "<null>";
            string guid = GLFW.GetJoystickGUID(jid) ?? "<null>";
            _ = GLFW.GetJoystickButtonsRaw(jid, out int btnCount);
            float* axsPtr = GLFW.GetJoystickAxesRaw(jid, out int axsCount);
            _ = GLFW.GetJoystickHatsRaw(jid, out int hatCount);
            string verdict = jid == joystickId
                ? "SELECTED"
                : RejectReason(name, btnCount, axsCount, axsPtr) ?? "eligible";
            logger.Notification(
                $"GamepadCompanion: {label} jid={jid} name=\"{name}\" guid={guid} " +
                $"buttons={btnCount} axes={axsCount} hats={hatCount} — {verdict}");
        }
        if (!foundAny)
            logger.Notification($"GamepadCompanion: {label} found no joysticks present");
    }

    private void OnGlfwError(ErrorCode error, string description)
    {
        logger.Warning($"GamepadCompanion: GLFW error {error}: {description}");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        ReleaseGamepad("provider disposed");
    }
}
