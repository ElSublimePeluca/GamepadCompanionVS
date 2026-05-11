using System;

namespace GamepadCompanion.Input;

public interface IGamepadProvider : IDisposable
{
    bool IsConnected { get; }
    string? DeviceName { get; }
    GamepadState Poll();
    float[] GetRawAxesSnapshot();
}
