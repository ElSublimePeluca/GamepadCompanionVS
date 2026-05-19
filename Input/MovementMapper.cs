using Vintagestory.API.Client;

namespace GamepadCompanion.Input;

// L stick → movement flags en EntityControls. Velocidad fija — para caminar
// lento se usa el toggle de sneak (M3). Solo escribimos true; respetamos
// el WASD del teclado.
public sealed class MovementMapper
{
    private const float Deadzone           = 0.2f;
    private const float DirectionThreshold = 0.3f;

    private readonly ICoreClientAPI capi;

    public MovementMapper(ICoreClientAPI capi)
    {
        this.capi = capi;
    }

    public void Apply(GamepadState current)
    {
        var controls = capi.World?.Player?.Entity?.Controls;
        if (controls is null) return;

        // FloorSitting bloquea el movimiento: al setear controls.Forward=true
        // el setter pasa por AttemptToggleAction → OnAction, pero ningún
        // handler vanilla cancela el move-while-seated (el teclado nunca
        // genera ese estado porque WASD primero unsitea). Sin este gate,
        // sentarse con DPad↓ y mover el stick L hacía caminar al personaje
        // sentado. Si el usuario quiere moverse, presiona DPad↓ otra vez
        // para levantarse.
        if (controls.FloorSitting) return;

        float x = current.LeftStickX;
        float y = current.LeftStickY;

        if (x * x + y * y < Deadzone * Deadzone) return;

        if (y >  DirectionThreshold) controls.Forward  = true;
        if (y < -DirectionThreshold) controls.Backward = true;
        if (x < -DirectionThreshold) controls.Left     = true;
        if (x >  DirectionThreshold) controls.Right    = true;
    }
}
