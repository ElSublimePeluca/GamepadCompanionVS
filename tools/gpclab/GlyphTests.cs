using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using GamepadCompanion.Actions;
using GamepadCompanion.Glyphs;
using GamepadCompanion.Input;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
// .NET 10 tambien trae un OrderedDictionary<,>; el del engine es el que va.
using VsOrderedDictionary = Vintagestory.API.Datastructures.OrderedDictionary<string, Vintagestory.API.Client.HotKey>;

namespace GamepadCompanion.Lab;

// Pruebas del mapa inverso hotkey → botón, sin el juego abierto.
//
// El resolver es la mitad del trabajo del issue #8 y la única que se puede
// probar de verdad offline: no dibuja nada, sólo contesta "qué botón hace esta
// tecla". Se lo alimenta con un ICoreClientAPI de mentira que tiene la tabla de
// hotkeys de vanilla y con un provider de mentira al que se le puede cambiar el
// nombre del mando.
internal static class GlyphTests
{
    private static int failures;

    public static int Run()
    {
        Console.WriteLine("\n  resolver de glifos");
        Triggers();
        TogglesAndButtons();
        UserOverrides();
        Collisions();
        Families();
        DeviceSwap();
        WholeLine();
        Inactive();
        Art();
        Patches();
        return failures;
    }

    // ── casos ─────────────────────────────────────────────────────────────────

    private static void Triggers()
    {
        var (resolver, config, _) = Build();
        Check("RT es el click izquierdo", resolver.LabelForMouse(0) == "RT", resolver.LabelForMouse(0));
        Check("LT es el click derecho",   resolver.LabelForMouse(2) == "LT", resolver.LabelForMouse(2));

        config.SwapTriggers = true;
        resolver.Invalidate();
        Check("con swap, el izquierdo pasa a LT", resolver.LabelForMouse(0) == "LT", resolver.LabelForMouse(0));
        Check("con swap, el derecho pasa a RT",   resolver.LabelForMouse(2) == "RT", resolver.LabelForMouse(2));
    }

    private static void TogglesAndButtons()
    {
        var (resolver, _, _) = Build();

        // L3/R3 son toggles en este mod: la etiqueta lleva la marca para no
        // prometer un "mantené RS" que en realidad es apretar-apretar-apretar.
        Check("LShift muestra R3 marcado como toggle",
              Key(resolver, GlKeys.LShift) == "RS" + GlyphLabels.ToggleMark, Key(resolver, GlKeys.LShift));
        Check("LControl muestra L3 marcado como toggle",
              Key(resolver, GlKeys.LControl) == "LS" + GlyphLabels.ToggleMark, Key(resolver, GlKeys.LControl));

        // Defaults de botón, resueltos contra el binding VIVO de cada hotkey.
        Check("E (inventorydialog) = Y",       Key(resolver, GlKeys.E) == "Y",    Key(resolver, GlKeys.E));
        Check("F (toolmodeselect) = X",        Key(resolver, GlKeys.F) == "X",    Key(resolver, GlKeys.F));
        Check("Escape (escapemenudialog) = Menu",
              Key(resolver, GlKeys.Escape) == "Menu", Key(resolver, GlKeys.Escape));
        Check("Space (jump) = A",              Key(resolver, GlKeys.Space) == "A", Key(resolver, GlKeys.Space));
        Check("G (sentarse) = D-Down",         Key(resolver, GlKeys.G) == "D-Down", Key(resolver, GlKeys.G));

        // El corto-circuito KeyCode == 50 → "Esc" del engine ocurre río arriba
        // del postfix, así que si el resolver no tuviera la entrada 50 el botón
        // Start se perdería en silencio. Es el caso que ninguna de las cuatro
        // propuestas de arquitectura vio.
        Check("Escape es el keycode 50", (int)GlKeys.Escape == 50, $"{(int)GlKeys.Escape}");
    }

    private static void UserOverrides()
    {
        var (resolver, _, ctx) = Build();
        // Un binding explícito del usuario le gana al default del mod...
        ctx.Buttons.Set(GamepadButton.DPadUp, new HotKeyAction("inventorydialog", "inv"));
        resolver.Invalidate();
        Check("un override de usuario le gana al default de otro botón",
              Key(resolver, GlKeys.E) == "D-Up", Key(resolver, GlKeys.E));

        // ...y también al toggle hardcodeado de L3/R3: si alguien se bindeó a
        // mano "mantener Shift" en un botón, el hint tiene que mostrar ESE botón,
        // y encima sin la marca de toggle, porque su binding sí es un hold.
        var (r2, _, ctx2) = Build();
        ctx2.Buttons.Set(GamepadButton.Y, new HoldKeyAction((int)GlKeys.LShift, shift: true, label: "hold shift"));
        r2.Invalidate();
        Check("un 'mantener Shift' bindeado a mano le gana al toggle de R3",
              Key(r2, GlKeys.LShift) == "Y", Key(r2, GlKeys.LShift));
    }

    private static void Collisions()
    {
        var (resolver, _, ctx) = Build();
        // Dos botones del MISMO nivel reclamando la misma tecla: el glifo sería
        // ambiguo, así que esa tecla vuelve al hint de teclado. Un hint verdadero
        // vale más que un glifo que miente.
        ctx.Buttons.Set(GamepadButton.DPadLeft,  new KeyPressAction((int)GlKeys.K, label: "k"));
        ctx.Buttons.Set(GamepadButton.DPadRight, new KeyPressAction((int)GlKeys.K, label: "k"));
        resolver.Invalidate();
        Check("dos botones que reclaman la misma tecla la mandan a vanilla",
              Key(resolver, GlKeys.K) is null, Key(resolver, GlKeys.K));
        Check("y queda registrada como colisión", resolver.Collisions.Count == 1,
              string.Join(" | ", resolver.Collisions));
        Check("el resto del mapa no se ve afectado", Key(resolver, GlKeys.E) == "Y", Key(resolver, GlKeys.E));
    }

    private static void Families()
    {
        // `auto` sale del NOMBRE del device, con el vendor id de desempate.
        var (r1, _, c1) = Build();
        c1.Provider.DeviceName = "GameSir-Cyclone 2";
        c1.Provider.VendorId = 0x054C;          // finge ser Sony por protocolo
        r1.Invalidate();
        Check("un GameSir sale Xbox aunque se declare vendor Sony",
              r1.Style == GlyphStyle.Xbox && Key(r1, GlKeys.E) == "Y", $"{r1.Style} / {Key(r1, GlKeys.E)}");

        // El mismo mando por USB: el driver xpad de Linux le borra el nombre y lo
        // deja como "Generic X-Box pad". Sólo queda el vendor id.
        var (rUsb, _, cUsb) = Build();
        cUsb.Provider.DeviceName = "Generic X-Box pad";
        cUsb.Provider.VendorId = 0x3537;
        rUsb.Invalidate();
        Check("un GameSir por USB (xpad le borra el nombre) sale Xbox por vendor id",
              rUsb.Style == GlyphStyle.Xbox, $"{rUsb.Style}");

        // El MISMO Cyclone 2 en modo PS4: se declara con el vendor id de Sony y el
        // nombre termina en "Wireless Controller", o sea que da de lleno en la
        // regla del DualShock 4. Lo salva que el nombre trae también la marca del
        // fabricante, y esa regla corre PRIMERO. Es una dependencia de ORDEN
        // dentro de FromDevice: si alguien reordena esos ifs, este mando pasa a
        // mostrar Cross/Square teniendo serigrafía ABXY. El string es el textual
        // que reportó el mando por Bluetooth.
        var (rPs4, _, cPs4) = Build();
        cPs4.Provider.DeviceName = "Guangzhou Chicken Run Network Technology Co., Ltd. Wireless Controller";
        cPs4.Provider.VendorId = 0x054C;
        rPs4.Invalidate();
        Check("el Cyclone 2 en modo PS4 sigue saliendo Xbox (la marca le gana al vendor Sony)",
              rPs4.Style == GlyphStyle.Xbox && Key(rPs4, GlKeys.Space) == "A",
              $"{rPs4.Style} / {Key(rPs4, GlKeys.Space)}");

        var (r2, _, c2) = Build();
        c2.Provider.DeviceName = "Wireless Controller";
        c2.Provider.VendorId = 0x054C;
        r2.Invalidate();
        Check("\"Wireless Controller\" + vendor Sony sale PlayStation",
              r2.Style == GlyphStyle.PlayStation, $"{r2.Style}");
        Check("y sus gatillos son L2/R2", r2.LabelForMouse(0) == "R2", r2.LabelForMouse(0));

        var (r3, cfg3, c3) = Build();
        c3.Provider.DeviceName = "Pro Controller";
        cfg3.GlyphStyle = "nintendo";
        r3.Invalidate();
        Check("forzado a nintendo, A y B están cambiados de lugar",
              Key(r3, GlKeys.Space) == "B", Key(r3, GlKeys.Space));
        Check("y los gatillos son ZL/ZR", r3.LabelForMouse(2) == "ZL", r3.LabelForMouse(2));

        var (r4, cfg4, _) = Build();
        cfg4.GlyphStyle = "esto no existe";
        r4.Invalidate();
        Check("un valor basura en el config cae en auto, no revienta",
              r4.ConfiguredStyle == GlyphStyle.Auto, $"{r4.ConfiguredStyle}");
    }

    // Cambiar el mando de modo lo re-enumera con otro nombre y otro vendor id. Si
    // el mapa sólo se rearmara con Invalidate, un flanco de conexión perdido lo
    // dejaría con las etiquetas del mando anterior.
    private static void DeviceSwap()
    {
        var (resolver, _, ctx) = Build();
        ctx.Provider.DeviceName = "Generic X-Box pad";
        ctx.Provider.VendorId = 0x3537;
        Check("arranca en Xbox", resolver.LabelForMouse(2) == "LT", resolver.LabelForMouse(2));

        // Sin Invalidate: sólo cambia el device, como si el flanco se perdiera.
        ctx.Provider.DeviceName = "Wireless Controller";
        ctx.Provider.VendorId = 0x054C;
        Check("cambiar de mando rearma el mapa sin Invalidate",
              resolver.LabelForMouse(2) == "L2", resolver.LabelForMouse(2));
    }

    // D6: en el cartel la conversión es TODO-O-NADA POR LÍNEA. La unidad de
    // percepción es la línea: media línea en glifos y media en teclado se lee
    // peor que la línea entera en teclado. Es la decisión que toma el prefix de
    // drawHelp, y es pura, así que se prueba sin el juego.
    private static void WholeLine()
    {
        var (resolver, _, ctx) = Build();

        var rightClick = new WorldInteraction
            { ActionLangCode = "abrir", MouseButton = EnumMouseButton.Right };
        Check("click derecho pelado: la línea entera se convierte",
              resolver.WholeLineConvertible(rightClick), "no");

        var shiftClick = new WorldInteraction
            { ActionLangCode = "apilar", MouseButton = EnumMouseButton.Right, HotKeyCode = "shift" };
        Check("shift + click derecho también (shift lo hace R3)",
              resolver.WholeLineConvertible(shiftClick), "no");

        // Una hotkey con FLAG de modificador no se sustituye nunca: el dibujo
        // agrega la cápsula "Ctrl" como literal y no hay forma de suprimirla, así
        // que saldría "[Ctrl] + [D-Down]", que es mentira. La línea entera cae.
        ctx.Hotkeys["dropitems"] = new HotKey
        {
            Code = "dropitems",
            CurrentMapping = new KeyCombination { KeyCode = (int)GlKeys.Q, Ctrl = true },
        };
        var ctrlLine = new WorldInteraction
            { ActionLangCode = "tirar", MouseButton = EnumMouseButton.Right, HotKeyCode = "dropitems" };
        Check("una hotkey con modificador tira la línea entera a teclado",
              !resolver.WholeLineConvertible(ctrlLine), "se convirtió");

        // Una tecla que ningún botón del mando hace: tampoco.
        ctx.Hotkeys["raro"] = new HotKey
        {
            Code = "raro",
            CurrentMapping = new KeyCombination { KeyCode = (int)GlKeys.Z },
        };
        var unmapped = new WorldInteraction { ActionLangCode = "raro", HotKeyCode = "raro" };
        Check("una tecla que ningún botón hace tira la línea entera",
              !resolver.WholeLineConvertible(unmapped), "se convirtió");

        // HotKeyCodes (plural) es el camino que usa el cartel cuando hay varias.
        var many = new WorldInteraction
        {
            ActionLangCode = "combo",
            MouseButton = EnumMouseButton.Right,
            HotKeyCodes = new[] { "shift", "raro" },
        };
        Check("con varias hotkeys alcanza que UNA no se pueda para caer entera",
              !resolver.WholeLineConvertible(many), "se convirtió");

        ctx.Provider.IsConnected = false;
        resolver.Invalidate();
        Check("sin mando, ninguna línea es convertible",
              !resolver.WholeLineConvertible(rightClick), "se convirtió");
    }

    private static void Inactive()
    {
        var (r1, cfg, _) = Build();
        cfg.GlyphStyle = "off";
        r1.Invalidate();
        Check("con GlyphStyle=off no se sustituye nada",
              !r1.Active && Key(r1, GlKeys.E) is null && r1.LabelForMouse(0) is null, "algo se sustituyó");

        var (r2, _, ctx) = Build();
        ctx.Provider.IsConnected = false;
        r2.Invalidate();
        Check("sin mando conectado tampoco",
              !r2.Active && Key(r2, GlKeys.E) is null, "algo se sustituyó");
    }

    // Los tres parches de Harmony, aplicados de verdad sobre los DLL del juego, en
    // este proceso y sin abrir el juego. Es el único test que ejercita
    // GlyphPatcher entero, y existe porque la primera versión tenía una
    // precondición circular — el postfix arrancaba con `if (!Rewrite.Usable)
    // return` y el canal sólo se marcaba usable DESPUÉS del self-test, así que no
    // podía pasar nunca — y encima dejaba ProbeLabel puesto al fallar, lo que
    // hacía que el resolver contestara siempre lo mismo y desaparecieran TODOS
    // los glifos, incluidos los de la etapa 2 que ya andaban.
    //
    // Corre último: parchea el engine en este proceso y ApplyOnce tiene un guard
    // estático, así que es de una sola vez.
    private static void Patches()
    {
        Console.WriteLine("\n  parches de Harmony (aplicados de verdad, sin el juego)");
        var (resolver, _, ctx) = Build();
        var session = new GlyphSession(ctx.Capi!, ctx.Config!, ctx.Provider,
                                       () => ctx.Buttons, () => ctx.Wheel, "test");
        // El resolver del test ya tiene la tabla de hotkeys cargada; la sesión
        // construye el suyo, así que le pasamos el mismo contexto.
        GlyphRuntime.Session = session;
        try
        {
            GlyphPatcher.ApplyOnce(ctx.Capi!, session, "gpclab-test");

            Check("el canal de sustitución queda activo",
                  session.Rewrite.Usable, session.Rewrite.Describe());
            Check("el canal del cartel queda activo", session.Sign.Usable, session.Sign.Describe());
            Check("el canal de prosa queda activo", session.Vtml.Usable, session.Vtml.Describe());

            // El bug que borró todos los glifos: la sonda tiene que quedar limpia
            // pase lo que pase.
            Check("la etiqueta de sonda no queda pegada",
                  session.Resolver.ProbeLabel is null, session.Resolver.ProbeLabel);

            var shift = new KeyCombination { KeyCode = (int)GlKeys.LShift };
            string outside = shift.PrimaryAsString();
            Check("fuera de la ventana no se sustituye nada", outside != "RS*", outside);

            var token = GlyphScope.Enter(sign: false);
            string inside = shift.PrimaryAsString();
            GlyphScope.Exit(token);
            Check("dentro de la ventana sí", inside == "RS*", inside);

            // ToString() es función de IDENTIDAD: lo usan el detector de
            // conflictos de Ajustes y HotkeyManager. Contaminarlo sería el peor
            // bug posible de todo esto.
            var token2 = GlyphScope.Enter(sign: false);
            string identity = shift.ToString();
            GlyphScope.Exit(token2);
            Check("ToString() queda intacto incluso dentro de la ventana",
                  !identity.Contains("RS*"), identity);

            // El volcado de `.gpglyphs`. Se prueba porque en el handler corre ANTES
            // del write al log: si tira, no queda ni línea de log ni mensaje en el
            // chat, o sea que el comando se ve como si no hiciera nada.
            try
            {
                string dump = GlyphDiagnostics.Describe(session);
                Check("el volcado de .gpglyphs no tira", dump.Length > 0, "vacío");
                string line = GlyphDiagnostics.Summarize(session);
                Check("el resumen de una línea entra en un mensaje de chat",
                      line.Length is > 20 and < 200, $"{line.Length} chars: {line}");
                Check("y menciona los cuatro canales",
                      dump.Contains("canal icons") && dump.Contains("canal rewrite")
                      && dump.Contains("canal sign") && dump.Contains("canal vtml"), dump);
            }
            catch (Exception e)
            {
                Check("el volcado de .gpglyphs no tira", false, e.ToString());
            }

            // Todo-o-nada: con la ventana abierta pero la línea marcada como no
            // convertible, tampoco se sustituye.
            var token3 = GlyphScope.Enter(sign: true, convertible: false);
            string blocked = shift.PrimaryAsString();
            GlyphScope.Exit(token3);
            Check("una línea no convertible no sustituye ni sus teclas",
                  blocked != "RS*", blocked);
        }
        finally
        {
            GlyphRuntime.Session = null;
            GlyphScope.ResetForFrame();
        }
    }

    // El arte de PlayStation se busca por la etiqueta que produjo el resolver
    // ("Cross", "Circle", …). Si alguien renombra una etiqueta en GlyphLabels, el
    // arte deja de encontrarse Y NO SE ROMPE NADA: sale el texto, silenciosamente.
    // Este test ata las dos tablas.
    private static void Art()
    {
        Console.WriteLine("\n  arte de las caras de PlayStation");
        var (resolver, config, _) = Build();
        config.GlyphStyle = "playstation";
        resolver.Invalidate();

        foreach (var (key, expected) in new[]
                 {
                     (GlKeys.Space, "Cross"),      // jump  → cara sur
                     (GlKeys.E,     "Triangle"),   // inventario → cara norte
                     (GlKeys.F,     "Square"),     // toolmode → cara oeste
                 })
        {
            string? label = Key(resolver, key);
            Check($"la etiqueta de {expected} sigue siendo la que busca el arte",
                  label == expected && GlyphArt.IsFaceLabel(label), label);
        }

        Check("una etiqueta que no es cara no dispara el arte",
              !GlyphArt.IsFaceLabel("L2") && !GlyphArt.IsFaceLabel("RS*"), "disparó");

    }


    // ── andamio ───────────────────────────────────────────────────────────────

    private static string? Key(GlyphResolver r, GlKeys key) =>
        r.LabelForCombination(new KeyCombination { KeyCode = (int)key });

    private sealed class FakeProvider : IGamepadProvider
    {
        public bool IsConnected { get; set; } = true;
        public string? DeviceName { get; set; } = "Xbox Controller";
        public int VendorId { get; set; }
        public string? PreferredDeviceName { get; set; }
        public GamepadState Poll() => GamepadState.Disconnected;
        public float[] GetRawAxesSnapshot() => Array.Empty<float>();
        public byte[] GetRawButtonsSnapshot() => Array.Empty<byte>();
        public IReadOnlyList<GamepadDeviceInfo> ScanDevices() => Array.Empty<GamepadDeviceInfo>();
        public bool SelectDevice(int jid) => false;
        public void ResetSelection() { }
        public void Dispose() { }
    }

    private sealed class Context
    {
        public FakeProvider Provider = new();
        public ButtonBindings Buttons = new();
        public SlotBindings Wheel = new(new IGameAction?[SlotBindings.SlotCount]);
        public Dictionary<string, HotKey> Hotkeys = new(StringComparer.OrdinalIgnoreCase);
        public ICoreClientAPI? Capi;
        public GamepadCompanionConfig? Config;
    }

    // Los bindings por default de vanilla que le importan al resolver.
    private static readonly (string Code, GlKeys Key)[] VanillaHotkeys =
    {
        ("primarymouse", 0), ("secondarymouse", 0),   // se reemplazan abajo por botones de mouse
        ("inventorydialog", GlKeys.E),
        ("toolmodeselect", GlKeys.F),
        ("worldmapdialog", GlKeys.M),
        ("escapemenudialog", GlKeys.Escape),
        ("jump", GlKeys.Space),
        ("sitdown", GlKeys.G),
        ("shift", GlKeys.LShift),
        ("ctrl", GlKeys.LControl),
        ("handbook", GlKeys.H),
        ("characterdialog", GlKeys.C),
    };

    private static (GlyphResolver, GamepadCompanionConfig, Context) Build()
    {
        var ctx = new Context();
        var config = new GamepadCompanionConfig();

        Dictionary<string, HotKey> hotkeys = ctx.Hotkeys;
        foreach (var (code, key) in VanillaHotkeys.Skip(2))
            hotkeys[code] = new HotKey { Code = code, CurrentMapping = new KeyCombination { KeyCode = (int)key } };
        // Los dos de mouse van por KeyCombination.MouseStart, igual que vanilla.
        hotkeys["primarymouse"] = new HotKey
            { Code = "primarymouse", CurrentMapping = new KeyCombination { KeyCode = KeyCombination.MouseStart + 0 } };
        hotkeys["secondarymouse"] = new HotKey
            { Code = "secondarymouse", CurrentMapping = new KeyCombination { KeyCode = KeyCombination.MouseStart + 2 } };

        var input = DispatchProxy.Create<IInputAPI, Stub>();
        ((Stub)(object)input).Handler = (m, a) => m.Name switch
        {
            "GetHotKeyByCode" => hotkeys.TryGetValue((string)a![0]!, out var hk) ? hk : null,
            "get_HotKeys"     => new VsOrderedDictionary(),
            _ => throw new NotSupportedException("IInputAPI." + m.Name),
        };

        // Un logger que no dice nada: los tests no leen el log, y sin esto la
        // sesión de glifos no se puede construir.
        var logger = DispatchProxy.Create<ILogger, Stub>();
        ((Stub)(object)logger).Handler = (_, _) => null;

        var gui = DispatchProxy.Create<IGuiAPI, Stub>();
        ((Stub)(object)gui).Handler = (m, _) => throw new NotSupportedException("IGuiAPI." + m.Name);

        var capi = DispatchProxy.Create<ICoreClientAPI, Stub>();
        ((Stub)(object)capi).Handler = (m, _) => m.Name switch
        {
            "get_Input"  => input,
            "get_Logger" => logger,
            "get_Gui"    => gui,
            _ => throw new NotSupportedException("ICoreClientAPI." + m.Name),
        };

        ctx.Capi = capi;
        ctx.Config = config;
        var resolver = new GlyphResolver(capi, config, ctx.Provider,
                                         () => ctx.Buttons, () => ctx.Wheel);
        return (resolver, config, ctx);
    }

    private static void Check(string what, bool ok, string? got)
    {
        if (!ok) failures++;
        Console.WriteLine($"    [{(ok ? "ok" : "NO")}] {what}" + (ok || got is null ? "" : $"  → {got}"));
    }
}
