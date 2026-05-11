using System;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Vintagestory.API.Common;

namespace GamepadCompanion.Input;

// Lee el joystick directamente vía GLFW joystick raw API — sin pasar por
// el sistema de "gamepad mapping" de GLFW (que rechazó el mapping del Cyclone 2
// silenciosamente, sin forma de diagnosticar). Esto asume layout xpad estándar
// de Linux: botones 0..10 en orden A,B,X,Y,LB,RB,Back,Start,Guide,L3,R3,
// axes 0..5 en orden LX,LY,LT,RX,RY,RT, dpad como axes 6/7 o como hat 0.
public sealed class GlfwGamepadProvider : IGamepadProvider
{
    private const int MaxJoysticks = 16;
    private const int DiagnosticIntervalPolls = 60;
    private const int MinExpectedButtons = 11;
    private const int MinExpectedAxes = 6;
    private const float DpadThreshold = 0.5f;

    private readonly ILogger logger;
    private readonly GLFWCallbacks.ErrorCallback errorCallback;
    private bool disposed;
    private int? joystickId;
    private int pollsWithoutGamepad;

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

        ushort bits = 0;

        // Botones 0..10 → A,B,X,Y,LB,RB,Back,Start,Guide,L3,R3 (orden xpad estándar).
        int btns = Math.Min(btnCount, 11);
        for (int i = 0; i < btns; i++)
            if (btnPtr[i] != JoystickInputAction.Release) bits |= (ushort)(1 << i);

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
        float leftX  =  axsPtr[0];
        float leftY  = -axsPtr[1];
        float rightX =  axsPtr[3];
        float rightY = -axsPtr[4];

        if (axsPtr[2] < -0.05f) ltRangeSigned = true;
        if (axsPtr[5] < -0.05f) rtRangeSigned = true;
        float leftT  = NormalizeTrigger(axsPtr[2], ltRangeSigned);
        float rightT = NormalizeTrigger(axsPtr[5], rtRangeSigned);

        return new GamepadState(bits, leftX, leftY, rightX, rightY, leftT, rightT);
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

    private void ReleaseGamepad(string reason)
    {
        if (joystickId is int jid)
            logger.Notification($"GamepadCompanion: gamepad jid={jid} released ({reason})");
        joystickId = null;
        DeviceName = null;
        pollsWithoutGamepad = 0;
        ltRangeSigned = false;
        rtRangeSigned = false;
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
