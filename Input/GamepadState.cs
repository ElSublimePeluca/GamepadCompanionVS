namespace GamepadCompanion.Input;

public readonly struct GamepadState
{
    public static readonly GamepadState Disconnected = default;

    public bool IsConnected { get; }
    public ushort ButtonBits { get; }
    public float LeftStickX { get; }
    public float LeftStickY { get; }
    public float RightStickX { get; }
    public float RightStickY { get; }
    public float LeftTrigger { get; }
    public float RightTrigger { get; }

    public GamepadState(
        ushort buttonBits,
        float leftX, float leftY,
        float rightX, float rightY,
        float leftTrigger, float rightTrigger)
    {
        IsConnected = true;
        ButtonBits = buttonBits;
        LeftStickX = leftX;
        LeftStickY = leftY;
        RightStickX = rightX;
        RightStickY = rightY;
        LeftTrigger = leftTrigger;
        RightTrigger = rightTrigger;
    }

    public bool IsDown(GamepadButton button)
        => IsConnected && (ButtonBits & (1 << (int)button)) != 0;

    public bool WasPressed(GamepadButton button, GamepadState previous)
        => IsDown(button) && !previous.IsDown(button);

    public bool WasReleased(GamepadButton button, GamepadState previous)
        => !IsDown(button) && previous.IsDown(button);
}
