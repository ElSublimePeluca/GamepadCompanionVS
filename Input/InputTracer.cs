using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using GamepadCompanion.Toggles;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;

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
    private readonly VirtualCursor cursor;

    private long endTimestamp;
    private long startTimestamp;
    private int polledFrames;

    public bool IsActive => endTimestamp != 0
        && Stopwatch.GetTimestamp() < endTimestamp;

    public InputTracer(ICoreClientAPI capi, IGamepadProvider gamepad,
                       ToggleManager toggles, VirtualCursor cursor)
    {
        this.capi = capi;
        this.gamepad = gamepad;
        this.toggles = toggles;
        this.cursor = cursor;
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

        AppendEngineState(line, controls);

        capi.Logger.Notification(line.ToString());
    }

    // Estado del engine que gatea los triggers. La v1 del tracer era ciega acá
    // y el "left trigger curse" (trace qdjXAamm) vivía justo en esta zona: el
    // mod leía LT=1.00 perfecto pero la interacción nunca salía. grab/anyw es
    // el gate real de SystemMouseInWorldInteractions; dlg el contador de
    // Dialog-type que ungrabea el mouse; fmk la tecla togglemousecontrol
    // (state+raw) que latcheada mata el grab sin dialog visible; imw lo que
    // efectivamente escribimos en InWorldMouseState; hand el HandUse que
    // también bloquea interacciones si queda colgado.
    private void AppendEngineState(StringBuilder line,
                                   Vintagestory.API.Common.EntityControls? controls)
    {
        if (capi.World is not ClientMain client)
        {
            line.Append(" eng=NULL");
            return;
        }

        int fmkCode = capi.Input.GetHotKeyByCode("togglemousecontrol")
            ?.CurrentMapping?.KeyCode ?? -1;
        var ks    = client.KeyboardState;
        var ksRaw = client.KeyboardStateRaw;
        bool fmkState = ks    is not null && fmkCode >= 0
            && fmkCode < ks.Length    && ks[fmkCode];
        bool fmkRaw   = ksRaw is not null && fmkCode >= 0
            && fmkCode < ksRaw.Length && ksRaw[fmkCode];

        line.Append(" eng=grab").Append(capi.Input.MouseGrabbed ? '1' : '0')
            .Append("/anyw").Append(client.mouseWorldInteractAnyway ? '1' : '0')
            .Append("/dlg").Append(client.DialogsOpened)
            .Append("/fmk").Append(fmkState ? '1' : '0').Append(fmkRaw ? '1' : '0')
            .Append("/imw").Append(client.InWorldMouseState?.Left  == true ? 'L' : '-')
                           .Append(client.InWorldMouseState?.Right == true ? 'R' : '-')
            .Append("/hand=").Append(controls?.HandUse.ToString() ?? "?")
            .Append("/paus").Append(capi.IsGamePaused ? '1' : '0');

        // Nombres de los Dialog-type abiertos (los que ungrabean el mouse): si
        // el curse resulta ser un dialog fantasma, esto lo nombra. "!" = está
        // en la lista pero IsOpened()==false (estado inconsistente, leak).
        if (client.DialogsOpened > 0)
        {
            line.Append("/dlgs=").Append(string.Join("+",
                capi.Gui.OpenedGuis
                    .Where(d => d is not null
                             && d.DialogType == EnumDialogType.Dialog)
                    .Select(d => d.GetType().Name + (d.IsOpened() ? "" : "!"))));
        }

        line.Append(" cur=v").Append(cursor.Visible ? '1' : '0')
            .Append("/o").Append(cursor.PhysicalOverride ? '1' : '0');
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
