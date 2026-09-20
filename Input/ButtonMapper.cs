using GamepadCompanion.Actions;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace GamepadCompanion.Input;

// Dispara por edge-press la acción de cada botón discreto del gamepad.
//
// Qué acción es sale de DOS capas, en este orden (ButtonBindings):
//   1. el override que el usuario asignó en la tab Botones, si hay;
//   2. si no, el default del LAYOUT activo (GamepadLayout: Clásico o Nuevo).
// El botón que el layout reserva para la rueda no hace ninguna de las dos.
//
// El "jump mientras se mantiene A" NO pasa por acá: lo proyecta MovementMapper
// junto con WASD, y queda unconditional (no se pierde aunque remapees A).
public sealed class ButtonMapper
{
    // Repetición al mantener, para la navegación del hotbar (BuiltinAction
    // .Repeats). Los números salen del comportamiento de un teclado: un retardo
    // largo para que un toque no dispare dos, y después ~8 por segundo, que es
    // cruzar los 10 slots en poco más de un segundo sin que se escape.
    private const float RepeatDelay    = 0.40f;
    private const float RepeatInterval = 0.12f;

    private readonly ICoreClientAPI capi;
    private readonly VirtualCursor cursor;

    // Cuánto falta para la próxima repetición, por botón. Sin entrada = el
    // botón no está manteniendo nada repetible.
    private readonly System.Collections.Generic.Dictionary<GamepadButton, float>
        repeatIn = new();

    public ButtonBindings Bindings { get; set; } = ButtonBindings.BuildDefault();

    public ButtonMapper(ICoreClientAPI capi, VirtualCursor cursor)
    {
        this.capi = capi;
        this.cursor = cursor;
    }

    public void Apply(GamepadState current, GamepadState previous, float dt)
    {
        // Los holds NO se aplican acá: el driver llama a ApplyHolds antes de
        // la etapa de triggers para que un modificador y el click que lo usa
        // puedan salir en el mismo tick (ver comentario en ApplyHolds).
        foreach (var btn in ButtonBindings.Configurable)
        {
            if (!current.WasPressed(btn, previous)) continue;
            var action = EffectiveFor(btn);
            // Los holdables ya los maneja ApplyHoldPresses — llamar Execute
            // acá sería un tap extra en cada press.
            if (action is IHoldableAction) continue;
            action?.Execute(capi);
        }

        ApplyRepeats(current, dt);
    }

    // Lo que este botón dispara AHORA, ya resuelto el contexto:
    //
    //   - El botón de la rueda no dispara nada: el hold ya significa abrirla.
    //     Se chequea antes que el binding del usuario a propósito, así un
    //     binding que quedó de otro layout queda dormido en vez de pelearse
    //     con la rueda.
    //   - Con el cursor virtual activo (diálogo modal abierto) el D-pad navega
    //     la UI desde GamepadInputDriver — salta de slot, hace zoom en el mapa
    //     — así que su DEFAULT no corre. Un binding explícito del usuario sí,
    //     porque es una elección suya y no una convención del mod.
    private IGameAction? EffectiveFor(GamepadButton btn)
    {
        var layout = Bindings.Layout;
        if (layout is not null && layout.IsWheel(btn)) return null;

        var user = Bindings[btn];
        if (user is not null) return user;

        if (cursor.Visible && IsDPad(btn)) return null;
        return layout?.Default(btn);
    }

    internal static bool IsDPad(GamepadButton btn) =>
        btn is GamepadButton.DPadUp or GamepadButton.DPadDown
            or GamepadButton.DPadLeft or GamepadButton.DPadRight;

    // Mantener el botón repite la acción, para las que lo piden (hotbar).
    // Pedido de pngwn: cruzar la barra con el bumper apretado en vez de doce
    // toques, que es como se hacía con AntiMicro.
    private void ApplyRepeats(GamepadState current, float dt)
    {
        foreach (var btn in ButtonBindings.Configurable)
        {
            var action = current.IsDown(btn) ? EffectiveFor(btn) : null;
            if (action is not BuiltinAction { Repeats: true })
            {
                repeatIn.Remove(btn);
                continue;
            }

            // Primer frame del hold: arranca el retardo inicial. El tap ya lo
            // ejecutó el edge de Apply, así que acá no se dispara nada todavía.
            if (!repeatIn.TryGetValue(btn, out float remaining))
            {
                repeatIn[btn] = RepeatDelay;
                continue;
            }

            remaining -= dt;
            // Cota: un frame largo (carga de chunks, alt-tab) puede acumular
            // medio segundo, y escupir veinte slots de golpe es peor que
            // perder repeticiones.
            for (int i = 0; i < 4 && remaining <= 0f; i++)
            {
                action.Execute(capi);
                remaining += RepeatInterval;
            }
            repeatIn[btn] = remaining > 0f ? remaining : RepeatInterval;
        }
    }

    // Instancia que efectivamente apretamos por botón — no alcanza con mirar
    // Bindings al soltar: el usuario puede reasignar el botón (o recargarse el
    // config entero) con la tecla apretada, y ahí la instancia vieja es la
    // única que sabe que tiene que soltar.
    private readonly System.Collections.Generic.Dictionary<GamepadButton,
        IHoldableAction> heldByButton = new();

    // Bindings "mientras esté apretado" (ej. mantener Alt para el modificador
    // de RKN Crafting): se manejan por edges de press y de release, no por
    // Execute. Las dos mitades están separadas porque el click las quiere en
    // ORDEN OPUESTO, y el driver las llama de los dos lados de triggers.Apply:
    //
    //   press  → tecla ANTES del click: si el usuario aprieta el botón del
    //            modificador y el gatillo en el mismo tick, el click tiene que
    //            salir con la tecla ya apretada. Al revés se pierde justo el
    //            frame que importa (el mod lee el modificador en el MouseDown).
    //   release → tecla DESPUÉS del click: soltando los dos a la vez, el
    //            MouseUp todavía tiene que ver el modificador vivo, igual que
    //            con un teclado donde el dedo del Alt no se levanta primero.
    //
    // Un tick que corta antes (radial, teclado virtual, foco perdido) llama a
    // ReleaseHolds() y no necesita ninguna de las dos.
    public void ApplyHoldPresses(GamepadState current, GamepadState previous)
    {
        foreach (var btn in ButtonBindings.Configurable)
        {
            // Ya lo estamos manteniendo: el release lo decide la otra mitad.
            if (heldByButton.ContainsKey(btn)) continue;
            // El botón de la rueda no ejecuta bindings (ver EffectiveFor).
            if (Bindings.Layout?.IsWheel(btn) == true) continue;
            if (Bindings[btn] is not IHoldableAction binding) continue;
            if (!current.IsDown(btn) || previous.IsDown(btn)) continue;

            // Registrar ANTES de apretar: Press pasa por el pipeline de
            // teclado del engine y dispara handlers de otros mods. Si alguno
            // termina llamando a ReleaseHolds(), tiene que encontrar la
            // entrada — si no, la tecla queda apretada sin dueño que la suelte.
            heldByButton[btn] = binding;
            binding.Press(capi);
        }
    }

    public void ApplyHoldReleases(GamepadState current)
    {
        System.Collections.Generic.List<GamepadButton>? release = null;
        foreach (var (btn, active) in heldByButton)
        {
            // Soltamos si el botón se levantó O si el binding cambió con el
            // botón todavía apretado (reasignar, recargar el config): ahí la
            // instancia vieja es la única que sabe que tiene que soltar. El
            // binding nuevo recién arranca en el próximo press edge — no lo
            // apretamos acá para no heredar una pulsación que no fue suya.
            if (current.IsDown(btn) &&
                ReferenceEquals(active, Bindings[btn] as IHoldableAction))
                continue;

            (release ??= new()).Add(btn);
        }

        if (release is null) return;

        // Soltar recién acá, fuera del foreach: ReleaseHold sale al engine y
        // un handler ajeno podría reentrar y mutar heldByButton, lo que
        // reventaría la iteración. Sacamos la entrada primero para que esa
        // reentrada no vuelva a soltar la misma tecla.
        foreach (var btn in release)
        {
            if (!heldByButton.Remove(btn, out var action)) continue;
            action.ReleaseHold(capi);
        }
    }

    // Soltar todo lo que esté siendo mantenido. Lo llama el driver en cada
    // camino donde deja de procesar botones (gamepad desconectado, ventana sin
    // foco, radial abierto, teclado virtual) y el ModSystem al descargarse:
    // sin esto el edge de release se perdería y la tecla quedaría latcheada.
    public void ReleaseHolds()
    {
        repeatIn.Clear();
        if (heldByButton.Count == 0) return;
        // Vaciar primero, soltar después: mismo motivo que en
        // ApplyHoldReleases — ReleaseHold sale al engine y una reentrada no
        // debe encontrarse iterando un diccionario que estamos mutando.
        var pending =
            new System.Collections.Generic.List<IHoldableAction>(
                heldByButton.Values);
        heldByButton.Clear();
        foreach (var action in pending) action.ReleaseHold(capi);
    }

    // Para el .gptrace: "A:AltLeft+X:LShift", o "-" si no hay ninguna. El
    // trace era ciego a los holds, y eso es exactamente lo que no se podía
    // decidir a distancia en el reporte de RKN Crafting: si el usuario bindeó
    // la entrada de tap o la de mantener, el log se veía igual.
    public string DescribeHolds()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var (btn, action) in heldByButton)
        {
            if (action.HeldKeyCode is not int code) continue;
            if (sb.Length > 0) sb.Append('+');
            sb.Append(btn).Append(':').Append((GlKeys)code);
        }
        return sb.Length == 0 ? "-" : sb.ToString();
    }

    // Keycodes que las acciones holdables mantienen apretados AHORA MISMO.
    // Lo consume ScreenInputMirror.Commit para reproyectar
    // ScreenManager.KeyboardKeyState por frame. Recibe la colección de afuera
    // para no allocar en cada frame de render.
    public void CollectHeldKeyCodes(
        System.Collections.Generic.ICollection<int> into)
    {
        foreach (var action in heldByButton.Values)
            if (action.HeldKeyCode is int code) into.Add(code);
    }

    // ¿Hay alguna acción holdable sosteniendo esta tecla ahora mismo? Lo
    // pregunta TriggerMapper antes de "curar" una tecla que cree fantasma.
    // Se resuelve contra heldByButton (no contra Bindings) por la misma razón
    // que el release: es la instancia que efectivamente apretamos, y se vacía
    // sola en ReleaseHolds() en todos los caminos de salida.
    public bool IsHoldingKeyCode(int keyCode)
    {
        if (keyCode < 0) return false;
        foreach (var action in heldByButton.Values)
            if (action.HeldKeyCode == keyCode) return true;
        return false;
    }
}
