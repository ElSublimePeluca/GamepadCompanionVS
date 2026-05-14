using System;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Vintagestory.API.Common;

namespace GamepadCompanion.Input;

// Lee el joystick directamente vía GLFW joystick raw API — sin pasar por
// el sistema de "gamepad mapping" de GLFW (que rechazó el mapping del Cyclone 2
// silenciosamente, sin forma de diagnosticar). Asume por default layout xpad:
// botones 0..10 en orden A,B,X,Y,LB,RB,Back,Start,Guide,L3,R3,
// axes 0..5 en orden LX,LY,LT,RX,RY,RT, dpad como axes 6/7 o como hat 0.
//
// Detecta tres layouts distintos vía firma:
//   Xpad        — XInput / xpad estándar. Default fallback.
//                 axes: LX,LY,LT,RX,RY,RT (0..5)
//                 buttons: A,B,X,Y,LB,RB,Back,Start,Guide,L3,R3 (0..10)
//   Ds4Amazon   — PS4-DInput "estándar Sony", visto en el control
//                 "Wired Controller" (Amazon B0CZ3WKF58). Firma: a3 o a4 ≈ -1
//                 al primer poll (triggers signed en posiciones 3/4 — los
//                 sticks no llegan a -1 en reposo, así que es exclusivo).
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

    private enum AxisLayout { Unknown, Xpad, Ds4Amazon, GameSirPs4 }

    // bit-index (orden GamepadButton) → raw button index. 11 entradas:
    // A,B,X,Y,LB,RB,Back,Start,Guide,L3,R3.
    private static readonly int[] Ds4AmazonButtonMap = {
        1,  2,  0,  3,  4,  5,  8,  9, 10, 11, 12,
    };
    private static readonly int[] GameSirPs4ButtonMap = {
        0,  1,  3,  2,  4,  5,  8,  9, 10, 11, 12,
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

    public bool IsConnected => joystickId.HasValue;
    public string? DeviceName { get; private set; }

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
            TryFindGamepad();
            if (!joystickId.HasValue)
            {
                if (pollsWithoutGamepad++ % DiagnosticIntervalPolls == 0)
                    LogDiagnosticScan();
                return GamepadState.Disconnected;
            }
            pollsWithoutGamepad = 0;
        }

        if (joystickId is not int jid) return GamepadState.Disconnected;

        return PollRaw(jid);
    }

    private unsafe void TryFindGamepad()
    {
        for (int jid = 0; jid < MaxJoysticks; jid++)
        {
            if (!GLFW.JoystickPresent(jid)) continue;

            _ = GLFW.GetJoystickButtonsRaw(jid, out int btnCount);
            _ = GLFW.GetJoystickAxesRaw(jid, out int axsCount);
            if (btnCount < MinExpectedButtons || axsCount < MinExpectedAxes)
                continue;

            string name = GLFW.GetJoystickName(jid) ?? "<unknown>";
            if (IsKnownNotGamepad(name)) continue;

            joystickId = jid;
            DeviceName = name;
            logger.Notification(
                $"GamepadCompanion: gamepad connected on jid={jid}: {DeviceName} " +
                $"(buttons={btnCount}, axes={axsCount})");
            return;
        }
    }

    // Joysticks-shaped que no son gamepads (teclados/mouse con botones programables
    // que el kernel también expone como /dev/input/js*). Crece con cada caso real.
    private static bool IsKnownNotGamepad(string name) =>
        name.Contains("Keychron", StringComparison.OrdinalIgnoreCase);

    private unsafe GamepadState PollRaw(int jid)
    {
        JoystickInputAction* btnPtr = GLFW.GetJoystickButtonsRaw(jid, out int btnCount);
        float* axsPtr = GLFW.GetJoystickAxesRaw(jid, out int axsCount);

        if (btnPtr == null || axsPtr == null)
        {
            ReleaseGamepad("null pointer from GLFW");
            return GamepadState.Disconnected;
        }

        DetectLayout(axsPtr, btnCount, DeviceName ?? string.Empty);

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
            default: // Xpad
                idxLT = 2; idxRT = 5; idxRX = 3; idxRY = 4;
                break;
        }

        float leftX  =  axsPtr[0];
        float leftY  = -axsPtr[1];
        float rightX =  axsPtr[idxRX];
        float rightY = -axsPtr[idxRY];

        if (axsPtr[idxLT] < -0.05f) ltRangeSigned = true;
        if (axsPtr[idxRT] < -0.05f) rtRangeSigned = true;
        float leftT  = NormalizeTrigger(axsPtr[idxLT], ltRangeSigned);
        float rightT = NormalizeTrigger(axsPtr[idxRT], rtRangeSigned);

        return new GamepadState(bits, leftX, leftY, rightX, rightY, leftT, rightT);
    }

    // Heurística sticky: una vez detectado, no vuelve a Unknown hasta desconexión.
    // Orden de chequeo (priorizamos signals más específicos):
    //   1. Nombre coincide con GameSir/Chicken Run → GameSirPs4. Necesario
    //      porque su firma de ejes (signed triggers a2/a5) coincide con xpad
    //      clásico de Linux, así que sin nombre no se puede distinguir.
    //   2. a3 o a4 < -0.9 al primer poll → Ds4Amazon. Firma exclusiva (los
    //      sticks no llegan a -1 en reposo, así que sólo aparece en layouts
    //      con triggers signed en posiciones 3/4).
    //   3. Default → Xpad.
    private unsafe void DetectLayout(float* axsPtr, int btnCount, string deviceName)
    {
        if (layout != AxisLayout.Unknown) return;

        if (deviceName.Contains("Chicken Run", StringComparison.OrdinalIgnoreCase) ||
            deviceName.Contains("GameSir",     StringComparison.OrdinalIgnoreCase))
        {
            layout = AxisLayout.GameSirPs4;
            ltRangeSigned = true;
            rtRangeSigned = true;
            logger.Notification(
                $"GamepadCompanion: detected GameSirPs4 layout by name (btnCount={btnCount})");
            return;
        }

        if (axsPtr[3] < Ds4LayoutThreshold || axsPtr[4] < Ds4LayoutThreshold)
        {
            layout = AxisLayout.Ds4Amazon;
            ltRangeSigned = true;
            rtRangeSigned = true;
            logger.Notification(
                $"GamepadCompanion: detected Ds4Amazon layout by signed triggers " +
                $"(a3={axsPtr[3]:F2}, a4={axsPtr[4]:F2}, btnCount={btnCount})");
            return;
        }

        layout = AxisLayout.Xpad;
    }

    private unsafe ushort ReadFaceButtons(JoystickInputAction* btnPtr, int btnCount)
    {
        ushort bits = 0;
        int[]? map = layout switch
        {
            AxisLayout.Ds4Amazon  => Ds4AmazonButtonMap,
            AxisLayout.GameSirPs4 => GameSirPs4ButtonMap,
            _                     => null,
        };

        if (map is not null)
        {
            for (int bit = 0; bit < map.Length; bit++)
            {
                int raw = map[bit];
                if (raw < btnCount && btnPtr[raw] != JoystickInputAction.Release)
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
    }

    private unsafe void LogDiagnosticScan()
    {
        bool foundAny = false;
        for (int jid = 0; jid < MaxJoysticks; jid++)
        {
            if (!GLFW.JoystickPresent(jid)) continue;
            foundAny = true;
            string name = GLFW.GetJoystickName(jid) ?? "<null>";
            string guid = GLFW.GetJoystickGUID(jid) ?? "<null>";
            _ = GLFW.GetJoystickButtonsRaw(jid, out int btnCount);
            _ = GLFW.GetJoystickAxesRaw(jid, out int axsCount);
            _ = GLFW.GetJoystickHatsRaw(jid, out int hatCount);
            logger.Notification(
                $"GamepadCompanion: scan jid={jid} name=\"{name}\" guid={guid} " +
                $"buttons={btnCount} axes={axsCount} hats={hatCount}");
        }
        if (!foundAny)
            logger.Notification("GamepadCompanion: scan found no joysticks present");
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
