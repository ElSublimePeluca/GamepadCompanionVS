using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using GamepadCompanion.Toggles;
using Vintagestory.API.Client;

namespace GamepadCompanion.Input;

// Diagnóstico: dump frame-por-frame al client-main.log durante N segundos,
// con raw buttons + raw axes + GamepadState mapeado + estado de toggles +
// flags de EntityControls relevantes a movimiento. Pensado para que un
// usuario externo lo corra durante el bug y nos mande el log.
//
// Logueamos vía Logger.Notification (no Debug) para que aparezca aun cuando
// el cliente está corriendo sin verbose flags. El cap del comando .gptrace
// limita la duración (default 15s, máx 60s) — a ~60 polls/s × 60s = ~3600
// líneas, manejable.
public sealed class InputTracer
{
    private const float DefaultSeconds = 15f;
    private const float MaxSeconds = 60f;

    private readonly ICoreClientAPI capi;
    private readonly IGamepadProvider gamepad;
    private readonly ToggleManager toggles;

    private long endTimestamp;
    private long startTimestamp;
    private int polledFrames;

    public bool IsActive => endTimestamp != 0
        && Stopwatch.GetTimestamp() < endTimestamp;

    public InputTracer(ICoreClientAPI capi, IGamepadProvider gamepad,
                       ToggleManager toggles)
    {
        this.capi = capi;
        this.gamepad = gamepad;
        this.toggles = toggles;
    }

    // Devuelve la duración efectiva (clampeada). Si ya había trace corriendo,
    // se reinicia: el endTimestamp se sobrescribe.
    public float Start(float seconds)
    {
        if (float.IsNaN(seconds) || seconds <= 0) seconds = DefaultSeconds;
        if (seconds > MaxSeconds) seconds = MaxSeconds;

        startTimestamp = Stopwatch.GetTimestamp();
        endTimestamp = startTimestamp
                     + (long)(seconds * Stopwatch.Frequency);
        polledFrames = 0;

        capi.Logger.Notification(
            $"GamepadCompanion gptrace: start duration={seconds:F1}s " +
            $"device=\"{gamepad.DeviceName ?? "<none>"}\" " +
            $"connected={gamepad.IsConnected}");
        return seconds;
    }

    public void Capture(GamepadState state)
    {
        long now = Stopwatch.GetTimestamp();
        if (endTimestamp == 0) return;
        if (now >= endTimestamp)
        {
            // Una sola línea de cierre, idempotente: endTimestamp se pone en 0
            // así no volvemos a entrar acá hasta el próximo .gptrace.
            capi.Logger.Notification(
                $"GamepadCompanion gptrace: end frames={polledFrames}");
            endTimestamp = 0;
            return;
        }

        polledFrames++;

        long elapsedMs = (now - startTimestamp) * 1000 / Stopwatch.Frequency;
        var line = new StringBuilder(256);
        line.Append("[gptrace t=").Append(elapsedMs).Append("ms] ");

        // Raw buttons como string de 0/1 (e.g. "010000000001000000"), para
        // que se vea fácil cuál bit raw cambia entre líneas consecutivas.
        byte[] rawBtns = gamepad.GetRawButtonsSnapshot();
        line.Append("rawBtn=");
        if (rawBtns.Length == 0) line.Append("[]");
        else
        {
            line.Append('[');
            for (int i = 0; i < rawBtns.Length; i++) line.Append(rawBtns[i]);
            line.Append(']');
        }

        // Lista de nombres de botones mapeados que están presionados.
        line.Append(" mapBtn=");
        line.Append(state.IsConnected
            ? FormatMappedButtons(state.ButtonBits)
            : "DISCONNECTED");

        // Raw axes con signo y 2 decimales.
        float[] rawAx = gamepad.GetRawAxesSnapshot();
        line.Append(" rawAx=[");
        for (int i = 0; i < rawAx.Length; i++)
        {
            if (i > 0) line.Append(' ');
            line.AppendFormat(System.Globalization.CultureInfo.InvariantCulture,
                              "{0:+0.00;-0.00;0.00}", rawAx[i]);
        }
        line.Append(']');

        // Sticks normalizados como los ven los mappers (post deadzone-less).
        line.AppendFormat(System.Globalization.CultureInfo.InvariantCulture,
            " ls={0:+0.00;-0.00;0.00}/{1:+0.00;-0.00;0.00}",
            state.LeftStickX, state.LeftStickY);
        line.AppendFormat(System.Globalization.CultureInfo.InvariantCulture,
            " rs={0:+0.00;-0.00;0.00}/{1:+0.00;-0.00;0.00}",
            state.RightStickX, state.RightStickY);
        line.AppendFormat(System.Globalization.CultureInfo.InvariantCulture,
            " trg={0:0.00}/{1:0.00}",
            state.LeftTrigger, state.RightTrigger);

        // Toggles del mod.
        line.Append(" tog=c").Append(toggles.CtrlActive ? '1' : '0')
            .Append("/s").Append(toggles.ShiftActive ? '1' : '0')
            .Append("/p").Append(toggles.PrecisionActive ? '1' : '0')
            .Append(toggles.Suspended ? "/SUSP" : "");

        // EntityControls relevantes al bug de movimiento.
        var controls = capi.World?.Player?.Entity?.Controls;
        if (controls is null) line.Append(" ec=NULL");
        else
        {
            line.Append(" ec=spr").Append(controls.Sprint  ? '1' : '0')
                .Append("/sn").Append(controls.Sneak   ? '1' : '0')
                .Append("/fwd").Append(controls.Forward  ? '1' : '0')
                .Append("/bwd").Append(controls.Backward ? '1' : '0')
                .Append("/lf").Append(controls.Left     ? '1' : '0')
                .Append("/rt").Append(controls.Right    ? '1' : '0');
        }

        capi.Logger.Notification(line.ToString());
    }

    private static string FormatMappedButtons(ushort bits)
    {
        if (bits == 0) return "-";
        return string.Join("+",
            Enum.GetValues<GamepadButton>()
                .Where(b => (bits & (1 << (int)b)) != 0)
                .Select(b => b.ToString()));
    }
}
