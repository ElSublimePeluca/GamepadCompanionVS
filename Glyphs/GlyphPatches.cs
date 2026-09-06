namespace GamepadCompanion.Glyphs;

using System;
using System.Collections.Generic;
using HarmonyLib;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.Client.NoObf;

// Los cuerpos de los tres parches. Tres reglas que valen para todo este archivo:
//
//   1. NUNCA tiran. Todo va en try/catch reportando al disyuntor de su canal.
//      Estos métodos corren DENTRO de la composición de un HUD del juego: una
//      excepción acá no rompe una GUI nuestra, rompe la del juego.
//   2. Toleran GlyphRuntime.Session == null. Los parches no se despatchean
//      JAMÁS, así que después de salir del mundo siguen instalados y tienen que
//      ser no-ops.
//   3. Sin estado propio: todo vive en la sesión, que se suelta en LeaveWorld.
internal static class GlyphPatches
{
    // El campo privado HotkeyComponent.hotkey, resuelto una vez por el patcher.
    internal static AccessTools.FieldRef<HotkeyComponent, HotKey>? HotkeyField;

    // ── P1 · la sustitución de texto ─────────────────────────────────────────
    //
    // KeyCombination.PrimaryAsString tiene exactamente DOS call sites en los cinco
    // assemblies del juego, y son los dos caminos del issue: HotkeyComponent
    // (los tags <hk>) y DrawWorldInteractionUtil (el cartel).
    //
    // Por qué acá y no en los lugares "obvios":
    //   · HotkeyComponent.DrawHotkey recibe el string YA TROCEADO — "Shift" es a
    //     la vez el nombre de la tecla LShift y el literal del modificador, así
    //     que son indistinguibles — y además GenHotkeyTexture ya dimensionó la
    //     superficie: medido, recorta entre 13 y 52 px.
    //   · GlKeyNames.ToString está río arriba de KeyCombination.ToString(), que es
    //     función de IDENTIDAD (el detector de conflictos de Ajustes compara esos
    //     strings). Y el corto-circuito `KeyCode == 50 → "Esc"` ocurre ANTES, así
    //     que un postfix ahí perdería el botón Start en silencio.
    internal static void PrimaryAsString_Postfix(KeyCombination __instance, ref string __result)
    {
        if (!GlyphScope.Open) return;
        GlyphSession? s = GlyphRuntime.Session;
        if (s is null || !s.Rewrite.Usable) return;
        try
        {
            string? label = s.Resolver.LabelForCombination(__instance);
            if (!string.IsNullOrEmpty(label)) __result = label!;
        }
        catch (Exception e) { s.Rewrite.ReportFailure(e); }
    }

    // ── P2 · el camino VTML <hk> ─────────────────────────────────────────────

    internal static void GenHotkeyTexture_Prefix(out GlyphScope.Token __state)
        => __state = GlyphScope.Enter(sign: false);

    // Finalizer y no postfix: corre también si el original tiró, así la ventana
    // NUNCA queda abierta — con la ventana abierta sí contaminaríamos Ajustes.
    // Y corre TODAVÍA DENTRO de la ventana (orden de Harmony: prefix → original →
    // postfix → finalizer), así que puede pedir el texto ya transformado; la
    // cierra en el finally.
    internal static void GenHotkeyTexture_Finalizer(HotkeyComponent __instance,
                                                    GlyphScope.Token __state)
    {
        GlyphSession? s = GlyphRuntime.Session;
        try
        {
            if (s is not null && s.Vtml.Usable) FixDisplayText(__instance);
        }
        catch (Exception e) { s?.Vtml.ReportFailure(e); }
        finally { GlyphScope.Exit(__state); }
    }

    // DisplayText es el ancho que el layout RESERVA, y se congela en el
    // constructor del componente: ningún ReCompose lo recalcula. La textura sí se
    // regenera. Sin corregirlo, al conectar el mando en caliente la cápsula cambia
    // de dibujo y el hueco reservado no — y queda un solape permanente.
    //
    // Funciona porque CalcBounds llama a GenHotkeyTexture ANTES de base.CalcBounds,
    // que es quien lineiza DisplayText: lo que escribamos acá lo toma esa misma
    // pasada.
    private static void FixDisplayText(HotkeyComponent component)
    {
        if (HotkeyField is null) return;
        HotKey? hotkey = HotkeyField(component);
        if (hotkey?.CurrentMapping is not KeyCombination m) return;

        var parts = new List<string>(4);
        // Los literales EXACTOS que agrega GenHotkeyTexture: "Ctrl"/"Alt"/"Shift".
        // OJO que NO son los de KeyCombination.ToString(), que usa mayúsculas.
        // Vanilla tiene ese desajuste; nosotros reservamos el ancho de lo que
        // realmente SE DIBUJA.
        if (m.Ctrl)  parts.Add("Ctrl");
        if (m.Alt)   parts.Add("Alt");
        if (m.Shift) parts.Add("Shift");
        parts.Add(m.PrimaryAsString());          // ya transformado: la ventana sigue abierta
        // GenHotkeyTexture dibuja la tecla secundaria con HasValue a secas, sin
        // pedirle además que sea > 0 como hace ToString(). El ancho hay que
        // reservarlo para lo que se dibuja, no para lo que ToString diría.
        if (m.SecondKeyCode.HasValue) parts.Add(m.SecondaryAsString());

        string text = string.Join(" + ", parts);
        // Nunca vacío: el layout lineiza DisplayText y después indexa la última
        // línea sin verificar que exista.
        if (!string.IsNullOrEmpty(text)) component.DisplayText = text;
    }

    // ── P4 · el arte de las caras de PlayStation ─────────────────────────────
    //
    // Reemplaza el TEXTO de la cápsula por el símbolo, sin tocar la caja: el ancho
    // lo sigue midiendo el string ("Cross"), así que el avance de retorno es
    // idéntico al de vanilla y nada de lo que ya se reservó río arriba se entera.
    //
    // Ojo que esto es lo contrario de lo que se descartó para sustituir TEXTO en
    // este mismo método: ahí el problema era que GenHotkeyTexture ya había
    // dimensionado la superficie con el string viejo. Acá el string no cambia,
    // sólo cambia lo que se pinta adentro.
    internal static bool DrawHotkey_Prefix(ICoreClientAPI capi, string keycode, double x, double y,
                                           Context ctx, CairoFont font, double lineheight,
                                           double textHeight, double pluswdith, double symbolspacing,
                                           double leftRightPadding, double[] color,
                                           ref double __result)
    {
        // Fuera de la ventana no tocamos nada: "Cross" sólo puede venir de una
        // sustitución nuestra, pero el gate es gratis y evita razonar sobre eso.
        if (!GlyphScope.Open || !GlyphArt.IsFaceLabel(keycode)) return true;
        GlyphSession? s = GlyphRuntime.Session;
        if (s is null || !s.Art.Usable) return true;

        try
        {
            __result = GlyphArt.DrawCapsule(capi, ctx, keycode, x, y, font, lineheight,
                                            textHeight, pluswdith, symbolspacing,
                                            leftRightPadding, color);
            return false;
        }
        catch (Exception e)
        {
            s.Art.ReportFailure(e);
            return true;                               // que siga vanilla con el texto
        }
    }

    // ── P3 · el cartel flotante ──────────────────────────────────────────────
    //
    // Se parchea drawHelp y no ComposeBlockWorldInteractionHelp porque drawHelp
    // corre dentro del closure de AddStaticCustomDraw, que en un ReCompose se
    // ejecuta FUERA de Compose…Help.
    internal readonly struct SignState
    {
        internal readonly GlyphScope.Token Token;
        internal readonly string LineKey;
        internal readonly bool Entered;
        internal SignState(GlyphScope.Token token, string lineKey, bool entered)
        {
            Token = token;
            LineKey = lineKey;
            Entered = entered;
        }
    }

    internal static void DrawHelp_Prefix(WorldInteraction wi, out SignState __state)
    {
        __state = default;
        GlyphSession? s = GlyphRuntime.Session;
        if (s is null || !s.Sign.Usable) return;
        try
        {
            string key = wi?.ActionLangCode ?? "";
            // Memoria de desborde POR LÍNEA: si esta línea concreta ya se pasó del
            // techo en esta sesión, no se convierte más. El resto del cartel sigue
            // con glifos.
            bool blocked = s.OverflowedLines.Contains(key);
            bool convertible = !blocked && s.Resolver.WholeLineConvertible(wi);
            __state = new SignState(GlyphScope.Enter(sign: true, convertible), key, entered: true);
        }
        catch (Exception e) { s.Sign.ReportFailure(e); }
    }

    // Guardia de desborde EN RUNTIME. Cada línea del cartel se dibuja en una caja
    // de 600 unidades y Cairo recorta en silencio; peor, ActualWidth es lo que
    // usa el HUD para CENTRAR el cartel, así que el síntoma no es "se corta" sino
    // "se corta a la derecha Y se corre". El peor caso de vanilla en alemán ya
    // está a 9 px del techo, así que no hay margen para confiar en una regla de
    // largo de string: se mide lo que realmente pasó.
    //
    // drawHelp corre UNA VEZ POR LÍNEA y ActualWidth se escribe al final, así que
    // acá la medición es exacta y es por línea.
    internal static void DrawHelp_Postfix(DrawWorldInteractionUtil __instance, SignState __state)
    {
        GlyphSession? s = GlyphRuntime.Session;
        if (s is null || !__state.Entered || !s.Sign.Usable) return;
        try
        {
            if (!GlyphScope.SignConvertible) return;      // no convertimos: no medimos
            double ceiling = 600.0 * Math.Max(0.01f, RuntimeEnv.GUIScale);
            if (__instance.ActualWidth <= ceiling * 0.97) return;

            // Esta línea se pasó: vuelve a teclado por el resto de la sesión y se
            // pide una recomposición. El jugador ve un frame feo una sola vez, y
            // sólo en ESA línea — no se apaga el canal entero.
            s.OverflowedLines.Add(__state.LineKey);
            s.Logger.Notification(
                "GamepadCompanion: glyph line '{0}' measured {1:0}px against a {2:0}px ceiling; " +
                "it falls back to keyboard hints for this session.",
                __state.LineKey, __instance.ActualWidth, ceiling);

            // Disyuntor sistémico: si se pasan muchas líneas distintas, lo que está
            // mal es global (un GUIScale raro, un idioma que nadie midió, un mod
            // con textos largos), no esta línea.
            if (s.OverflowedLines.Count >= 8)
                s.Sign.MarkUnavailable("8 líneas del cartel se pasaron del techo de ancho");

            s.Invalidator?.RequestRefresh();
        }
        catch (Exception e) { s.Sign.ReportFailure(e); }
    }

    internal static void DrawHelp_Finalizer(SignState __state)
    {
        if (__state.Entered) GlyphScope.Exit(__state.Token);
    }
}
