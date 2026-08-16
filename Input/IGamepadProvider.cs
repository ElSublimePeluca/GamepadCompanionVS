using System;
using System.Collections.Generic;

namespace GamepadCompanion.Input;

// Un joystick visible para GLFW, sea o no un gamepad de verdad. Lo devuelve
// ScanDevices para que el usuario pueda elegir a mano cuál usar cuando la
// autodetección se equivoca (ver .gpdevice).
public readonly record struct GamepadDeviceInfo(
    int Jid, string Name, int Buttons, int Axes, int Hats, bool Eligible,
    string? RejectReason, bool Selected);

public interface IGamepadProvider : IDisposable
{
    bool IsConnected { get; }
    string? DeviceName { get; }

    // Substring (case-insensitive) del nombre del device que el usuario fijó
    // a mano. null = autodetección. Se persiste en el config del mod.
    string? PreferredDeviceName { get; set; }

    GamepadState Poll();
    float[] GetRawAxesSnapshot();
    byte[] GetRawButtonsSnapshot();

    // Todos los joysticks presentes ahora mismo, con el veredicto de la
    // autodetección para cada uno.
    IReadOnlyList<GamepadDeviceInfo> ScanDevices();

    // Fuerza el device activo. Devuelve false si ese jid no está presente.
    bool SelectDevice(int jid);

    // Suelta el device actual para que el próximo poll rehaga la selección.
    void ResetSelection();
}
