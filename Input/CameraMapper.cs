using System;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;

namespace GamepadCompanion.Input;

// R stick → cámara. Inyectamos delta en ClientMain.MouseDeltaX/Y, que es lo que
// el engine procesa cada frame para mover la cámara (igual que el mouse físico).
//
// Probado y descartado: entity.Pos.Yaw/Pitch, capi.Input.MouseYaw/MousePitch e
// IClientPlayer.CameraYaw/Pitch — todos son outputs derivados, sobreescritos
// cada frame. ClientMain.MouseDeltaX/Y es la fuente real (delta crudo del mouse).
public sealed class CameraMapper
{
    private readonly ICoreClientAPI capi;
    private readonly GamepadCompanionConfig config;

    public CameraMapper(ICoreClientAPI capi, GamepadCompanionConfig config)
    {
        this.capi = capi;
        this.config = config;
    }

    public void Apply(GamepadState current, float dt, float precisionFactor = 1f)
    {
        if (capi.World is not ClientMain client) return;

        float x = current.RightStickX;
        float y = current.RightStickY;
        float dz = config.Deadzone;

        if (x * x + y * y < dz * dz) return;

        float fx = SignedSquareWithDeadzone(x, dz);
        float fy = SignedSquareWithDeadzone(y, dz);

        float pitchSign = config.InvertPitch ? -1f : +1f;

        client.MouseDeltaX += fx * config.YawSensitivity * dt * precisionFactor;
        client.MouseDeltaY += fy * pitchSign * config.PitchSensitivity
                              * dt * precisionFactor;
    }

    private static float SignedSquareWithDeadzone(float v, float dz)
    {
        float abs = MathF.Abs(v);
        if (abs < dz) return 0;
        float t = (abs - dz) / (1f - dz);
        return MathF.Sign(v) * t * t;
    }
}
