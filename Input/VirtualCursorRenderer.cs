using Vintagestory.API.Client;

namespace GamepadCompanion.Input;

// Dibuja el cursor virtual encima de la UI cuando está visible. Cross
// amarillo con outline negro para legibilidad sobre cualquier fondo.
//
// Render stage Done: corre DESPUÉS de Ortho (donde se dibujan dialogs y
// HUDs), por eso queda por encima. Ortho dejaría el cursor tapado por
// los dialogs aunque usemos RenderOrder=1.0.
public sealed class VirtualCursorRenderer : IRenderer
{
    private readonly ICoreClientAPI capi;
    private readonly VirtualCursor cursor;

    public double RenderOrder => 1.0; // máximo, dibujamos al final
    public int RenderRange => 0;

    public VirtualCursorRenderer(ICoreClientAPI capi, VirtualCursor cursor)
    {
        this.capi = capi;
        this.cursor = cursor;
    }

    public void OnRenderFrame(float dt, EnumRenderStage stage)
    {
        // PhysicalOverride: el usuario tomó el mouse físico, escondemos el cross
        // amarillo para no mostrar dos cursores. Al retomar con el gamepad
        // vuelve a aparecer.
        if (!cursor.Visible || cursor.PhysicalOverride) return;

        // En Done el shader gui no está bound ni hay proyección
        // ortográfica activa (la engine ya hizo guiShader.Stop() +
        // PerspectiveMode() después de la stage Ortho). Hay que
        // activarlos manualmente o RenderRectangle crashea con
        // "Can't set uniform on not active shader gui!".
        //
        // El pair balanceado es OrthoMode ↔ PerspectiveMode. OrthoMode
        // hace 2 GlPushMatrix (projection + modelview) y PerspectiveMode
        // hace 2 GlPopMatrix. Reset3DProjection NO popea — solo recarga
        // la matriz, así que usarlo aquí leakea 2 entries por frame y
        // termina en "Stack matrix overflow".
        var guiShader = capi.Render.GetEngineShader(EnumShaderProgram.Gui);
        guiShader.Use();
        capi.Render.OrthoMode(capi.Render.FrameWidth, capi.Render.FrameHeight);
        // Done stage corre tras PerspectiveMode() que reactiva DepthFunc=Less,
        // así que sin esto los rects con z>0 fallan el test contra los píxeles
        // de los dialogs ya dibujados y desaparecen.
        capi.Render.GLDisableDepthTest();
        try
        {
            // Cross simple: rectangle horizontal y vertical. Tamaño y grosor
            // visibles en cualquier resolución; escalar con GUIScale sería
            // ideal pero está OK para empezar.
            const float ArmLen = 14f;
            const float Thick = 2f;
            const float OutlineExtra = 2f;

            int colorFill    = unchecked((int)0xFFFFCC33);
            int colorOutline = unchecked((int)0xFF000000);

            DrawArm(cursor.X, cursor.Y, ArmLen, Thick + OutlineExtra,
                    horizontal: true,  z: 50f, color: colorOutline);
            DrawArm(cursor.X, cursor.Y, ArmLen, Thick + OutlineExtra,
                    horizontal: false, z: 50f, color: colorOutline);
            DrawArm(cursor.X, cursor.Y, ArmLen - 1, Thick,
                    horizontal: true,  z: 51f, color: colorFill);
            DrawArm(cursor.X, cursor.Y, ArmLen - 1, Thick,
                    horizontal: false, z: 51f, color: colorFill);
        }
        finally
        {
            capi.Render.GLEnableDepthTest();
            capi.Render.PerspectiveMode();
            guiShader.Stop();
        }
    }

    private void DrawArm(float cx, float cy, float armLen, float thick,
                         bool horizontal, float z, int color)
    {
        float w = horizontal ? armLen * 2f : thick;
        float h = horizontal ? thick : armLen * 2f;
        float x = cx - w / 2f;
        float y = cy - h / 2f;
        capi.Render.RenderRectangle(x, y, z, w, h, color);
    }

    public void Dispose() { }
}
