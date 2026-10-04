using System;
using System.Collections.Generic;
using GamepadCompanion.Actions;
using GamepadCompanion.Gui;
using GamepadCompanion.Input;
using Vintagestory.API.Client;

namespace GamepadCompanion.Lab;

// El D-pad y la rueda con un menú abierto (reporte en ModDB contra 1.14.0).
//
// Con un menú abierto el D-pad mueve el cursor virtual, y las dos cosas que se
// reportaron son las dos caras de eso:
//
//   1. Lo que el usuario le asignó al D-pad corría IGUAL, además de mover el
//      cursor: con D-pad ↓ = mantener Z para ZoomButton, bajar un slot en el
//      inventario hacía zoom.
//   2. La rueda del layout nuevo está en D-pad ↑ y con un menú abierto no se
//      abría nunca. Con el inventario en la rueda, abrir un cofre y el
//      inventario a la vez era imposible; y como el chat enfocado cuenta como
//      menú, tampoco había cómo abrir el teclado virtual para escribir en él.
//
// Lo que se fija acá es la regla de cada uno, sin el juego.
internal static class MenuDPadTests
{
    private static int failures;

    // 60 fps. Los tiempos se miden en frames y se comparan con tolerancia de
    // uno: sumar 1/60 en float no llega exacto a 0.4.
    private const float Frame = 1f / 60f;

    public static int Run()
    {
        Console.WriteLine("\n  el D-pad con un menú abierto");
        Bindings();
        Holds();
        Console.WriteLine("\n  abrir la rueda con un menú abierto");
        Opening();
        Sequences();
        return failures;
    }

    private static void Bindings()
    {
        var bindings = new ButtonBindings
        {
            Layout = GamepadLayout.Build(GamepadLayoutKind.Modern, LayoutTests.FakeApi()),
        };
        var zoom = new FakeHold();
        var mine = new HotKeyAction("handbook", "manual");
        bindings.Set(GamepadButton.DPadDown, zoom);
        bindings.Set(GamepadButton.Y, mine);

        Check("en el mundo, lo asignado al D-pad corre",
              ReferenceEquals(ButtonMapper.Resolve(bindings, GamepadButton.DPadDown, menuOpen: false), zoom));
        Check("en un menú no: el D-pad sólo mueve el cursor (el caso de ZoomButton)",
              ButtonMapper.Resolve(bindings, GamepadButton.DPadDown, menuOpen: true) is null);
        Check("los defaults del D-pad tampoco, como antes",
              ButtonMapper.Resolve(bindings, GamepadButton.DPadLeft,  menuOpen: true) is null &&
              ButtonMapper.Resolve(bindings, GamepadButton.DPadRight, menuOpen: true) is null);
        Check("los demás botones siguen andando en un menú, asignados o de fábrica",
              ReferenceEquals(ButtonMapper.Resolve(bindings, GamepadButton.Y, menuOpen: true), mine) &&
              (ButtonMapper.Resolve(bindings, GamepadButton.LeftBumper, menuOpen: true)
                   as BuiltinAction)?.Code == "hotbarPrev");

        bindings.Set(GamepadButton.DPadUp, mine);
        Check("el botón de la rueda no dispara nada, tampoco en el mundo",
              ButtonMapper.Resolve(bindings, GamepadButton.DPadUp, menuOpen: false) is null);
    }

    // Con un ButtonMapper de verdad y la misma secuencia del driver por tick:
    // ApplyHoldPresses, Apply, ApplyHoldReleases.
    private static void Holds()
    {
        var buttons = new ButtonMapper(LayoutTests.FakeApi())
        {
            Bindings = new ButtonBindings
            {
                Layout = GamepadLayout.Build(GamepadLayoutKind.Modern, LayoutTests.FakeApi()),
            },
        };
        var zoom = new FakeHold();
        buttons.Bindings.Set(GamepadButton.DPadDown, zoom);

        GamepadState idle = Pad(), down = Pad(GamepadButton.DPadDown);

        Tick(buttons, idle, down, menuOpen: true);
        Check("en un menú, apretar D-pad ↓ no aprieta la tecla", zoom.Presses == 0);
        Tick(buttons, down, idle, menuOpen: true);

        Tick(buttons, idle, down, menuOpen: false);
        Check("en el mundo sí", zoom.Presses == 1 && zoom.Held);

        // Abrir el inventario con la tecla apretada no la suelta: lo mismo que
        // hace un teclado. La suelta el botón.
        Tick(buttons, down, down, menuOpen: true);
        Check("un hold que venía del mundo sigue al abrirse un menú",
              zoom.Held && zoom.Releases == 0);
        Tick(buttons, down, idle, menuOpen: true);
        Check("y se suelta al soltar el botón", !zoom.Held && zoom.Releases == 1);

        // El que apretó en el menú y lo cerró sin soltar no apretó para hacer
        // zoom: hace falta otro flanco, como con el salto de A.
        Tick(buttons, idle, down, menuOpen: true);
        Tick(buttons, down, down, menuOpen: false);
        Check("cerrar el menú con el botón apretado no la aprieta a destiempo",
              zoom.Presses == 1);
    }

    private static void Opening()
    {
        Check("clásico: LB abre al apretar en todos lados, como siempre",
              RadialMenuDialog.OpeningFor(GamepadButton.LeftBumper, false, false) == WheelOpening.OnPress &&
              RadialMenuDialog.OpeningFor(GamepadButton.LeftBumper, true,  false) == WheelOpening.OnPress &&
              RadialMenuDialog.OpeningFor(GamepadButton.LeftBumper, false, true)  == WheelOpening.OnPress);
        Check("nuevo, en el mundo: D-pad ↑ abre al apretar",
              RadialMenuDialog.OpeningFor(GamepadButton.DPadUp, false, false) == WheelOpening.OnPress);
        Check("nuevo, con un menú abierto: hay que mantenerlo",
              RadialMenuDialog.OpeningFor(GamepadButton.DPadUp, true, false) == WheelOpening.OnHold);
        Check("nuevo, con el teclado virtual: nunca, el D-pad es del teclado",
              RadialMenuDialog.OpeningFor(GamepadButton.DPadUp, false, true) == WheelOpening.Never &&
              RadialMenuDialog.OpeningFor(GamepadButton.DPadUp, true,  true) == WheelOpening.Never);
    }

    private static void Sequences()
    {
        const WheelOpening P = WheelOpening.OnPress, H = WheelOpening.OnHold, N = WheelOpening.Never;

        Expect("en el mundo abre en el mismo frame en que se aprieta",
               opensAt: 1, Up(P), Down(P, 5));
        Expect("en un menú un toque no la abre (es del cursor)",
               opensAt: null, Up(H), Down(H, 10), Up(H, 30));
        ExpectAfterHold("en un menú, mantenerlo la abre a los 0,4 s",
                        Up(H), Down(H, 60));
        Expect("dos toques cortos seguidos no suman",
               opensAt: null, Up(H), Down(H, 18), Up(H), Down(H, 18), Up(H, 30));
        Expect("con el teclado virtual no abre por más que se mantenga",
               opensAt: null, Up(N), Down(N, 120));
        Expect("un botón apretado desde antes no la abre al entrar a un menú",
               opensAt: null, Down(H, 120));
        // Lo está manteniendo para abrirla: que el menú se cierre en el medio
        // no es motivo para tirar la espera, ni para abrirla antes de tiempo.
        ExpectAfterHold("si el menú se cierra en medio de la espera, abre al cumplirla",
                        Up(H), Down(H, 10), Down(P, 60));
        Expect("si en el medio aparece el teclado virtual, la espera se cancela",
               opensAt: null, Up(H), Down(H, 10), Down(N, 1), Down(H, 60));
    }

    // ── andamio ───────────────────────────────────────────────────────────────

    private static void Tick(ButtonMapper buttons, GamepadState previous,
                             GamepadState current, bool menuOpen)
    {
        buttons.ApplyHoldPresses(current, previous, menuOpen);
        buttons.Apply(current, previous, Frame, menuOpen);
        buttons.ApplyHoldReleases(current);
    }

    private static GamepadState Pad(params GamepadButton[] down)
    {
        ushort bits = 0;
        foreach (var b in down) bits |= (ushort)(1 << (int)b);
        return new GamepadState(bits, 0, 0, 0, 0, 0, 0);
    }

    private static (bool Down, WheelOpening Mode)[] Up(WheelOpening mode, int frames = 1)
        => Repeat(false, mode, frames);

    private static (bool Down, WheelOpening Mode)[] Down(WheelOpening mode, int frames)
        => Repeat(true, mode, frames);

    private static (bool, WheelOpening)[] Repeat(bool down, WheelOpening mode, int frames)
    {
        var run = new (bool, WheelOpening)[frames];
        for (int i = 0; i < frames; i++) run[i] = (down, mode);
        return run;
    }

    // En qué frame se abriría la rueda (null = nunca), con el mismo paso que
    // corre RadialMenuDialog.OnGamepadTick mientras está cerrada. El frame 0
    // no tiene anterior: arranca como si el botón viniera suelto, salvo que el
    // primer tramo ya esté apretado (el caso "apretado desde antes").
    private static (int? OpensAt, int PressedAt) Simulate(
        params (bool Down, WheelOpening Mode)[][] runs)
    {
        var frames = new List<(bool Down, WheelOpening Mode)>();
        foreach (var run in runs) frames.AddRange(run);

        float? waited = null;
        int pressedAt = -1;
        bool wasDown = frames.Count > 0 && frames[0].Down;
        for (int i = 0; i < frames.Count; i++)
        {
            var (down, mode) = frames[i];
            if (down && !wasDown) pressedAt = i;
            (waited, bool open) = RadialMenuDialog.StepOpening(waited, down, wasDown, mode, Frame);
            if (open) return (i, pressedAt);
            wasDown = down;
        }
        return (null, pressedAt);
    }

    private static void Expect(string what, int? opensAt,
                               params (bool, WheelOpening)[][] runs)
    {
        var (got, _) = Simulate(runs);
        bool ok = got == opensAt;
        if (!ok) failures++;
        Console.WriteLine($"    [{(ok ? "ok" : "NO")}] {what}"
                          + (ok ? "" : $"  → abre en el frame {got?.ToString() ?? "nunca"}"));
    }

    // Abre cuando se cumple HoldToOpen desde el flanco: ni antes, ni más de un
    // frame después.
    private static void ExpectAfterHold(string what, params (bool, WheelOpening)[][] runs)
    {
        var (got, pressedAt) = Simulate(runs);
        float? held = got is int f ? (f - pressedAt) * Frame : null;
        bool ok = held is float h
                  && h >= RadialMenuDialog.HoldToOpen - 0.001f
                  && h <= RadialMenuDialog.HoldToOpen + Frame + 0.001f;
        if (!ok) failures++;
        Console.WriteLine($"    [{(ok ? "ok" : "NO")}] {what}"
                          + (ok ? "" : $"  → {(held is float x ? $"abrió a los {x:0.000} s" : "no abrió")}"));
    }

    private static void Check(string what, bool ok)
    {
        if (!ok) failures++;
        Console.WriteLine($"    [{(ok ? "ok" : "NO")}] {what}");
    }

    // Un "mantener tecla" que sólo cuenta. El de verdad (HoldKeyAction) sale al
    // pipeline de teclado de ClientMain, que acá no existe.
    private sealed class FakeHold : IGameAction, IHoldableAction
    {
        public int Presses, Releases;
        public bool Held;
        public string Label => "mantener Z";
        public int? HeldKeyCode => Held ? (int)GlKeys.Z : null;
        public void Execute(ICoreClientAPI capi) { }
        public void Press(ICoreClientAPI capi) { Held = true; Presses++; }
        public void ReleaseHold(ICoreClientAPI capi)
        {
            if (!Held) return;
            Held = false;
            Releases++;
        }
    }
}
