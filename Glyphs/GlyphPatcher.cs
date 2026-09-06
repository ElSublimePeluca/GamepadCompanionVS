namespace GamepadCompanion.Glyphs;

using System;
using System.Linq;
using System.Reflection;
using Cairo;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client.NoObf;

// Aplica los tres parches, una sola vez por proceso, a mano.
//
// Nunca [HarmonyPatch] + PatchAll: PatchAll tira HarmonyException si un target no
// existe, y una excepción dentro de StartClientSide no degrada el mod, lo saca
// entero de enabledSystems — sin gamepad y sin Dispose. Con AccessTools + null
// check por canal, un método renombrado se lleva SU canal y nada más.
//
// Y nunca UnpatchAll: Dispose puede no correr jamás, el assembly se carga una
// sola vez por proceso y Harmony no deduplica. El guard estático es lo que evita
// que recargar el mundo apile parches.
internal static class GlyphPatcher
{
    private static bool attempted;
    // Resultado del único intento, para poder repetirlo en las sesiones
    // siguientes sin volver a parchear.
    private static string? rewriteFailure, vtmlFailure, signFailure, artFailure, sharedNote;

    internal static void ApplyOnce(ICoreClientAPI capi, GlyphSession session, string harmonyId)
    {
        if (attempted) { Adopt(session); return; }
        attempted = true;                       // aunque falle: no reintentar por mundo

        try
        {
            var harmony = new Harmony(harmonyId);

            // Detección por FIRMA EXACTA. AccessTools.Method devuelve null en vez
            // de tirar, que es justo lo que hace falta acá.
            MethodInfo? primary = AccessTools.Method(typeof(KeyCombination), "PrimaryAsString", Type.EmptyTypes);
            MethodInfo? genTex  = AccessTools.Method(typeof(HotkeyComponent), "GenHotkeyTexture", Type.EmptyTypes);
            FieldInfo?  hotkey  = AccessTools.Field(typeof(HotkeyComponent), "hotkey");
            MethodInfo? drawHotkey = AccessTools.Method(typeof(HotkeyComponent), "DrawHotkey", new[]
            {
                typeof(ICoreClientAPI), typeof(string), typeof(double), typeof(double),
                typeof(Context), typeof(CairoFont), typeof(double), typeof(double),
                typeof(double), typeof(double), typeof(double), typeof(double[]),
            });
            MethodInfo? drawHelp = AccessTools.Method(typeof(DrawWorldInteractionUtil), "drawHelp", new[]
            {
                typeof(Context), typeof(ImageSurface), typeof(ElementBounds),
                typeof(ItemStack[]), typeof(double), typeof(WorldInteraction),
            });
            FieldInfo? actualWidth = AccessTools.Field(typeof(DrawWorldInteractionUtil), "ActualWidth");

            // ── P1 ──
            if (primary is null || primary.ReturnType != typeof(string))
            {
                Fail(session, ref rewriteFailure, session.Rewrite,
                     "KeyCombination.PrimaryAsString() cambió de firma");
                return;   // sin P1 los otros dos no tienen nada que habilitar
            }
            harmony.Patch(primary, postfix: Ref(nameof(GlyphPatches.PrimaryAsString_Postfix)));
            // El canal se marca activo ACÁ y no después del self-test: el cuerpo
            // del postfix arranca con `if (!s.Rewrite.Usable) return`, así que con
            // el canal en "pendiente" la sonda no podía dar positivo ni aunque el
            // parche estuviera perfecto. Si el self-test falla, lo degrada.
            session.Rewrite.MarkActive();

            // ── P2 ──
            if (genTex is null || hotkey is null || hotkey.FieldType != typeof(HotKey))
                Fail(session, ref vtmlFailure, session.Vtml,
                     "HotkeyComponent.GenHotkeyTexture()/hotkey cambiaron");
            else
            {
                GlyphPatches.HotkeyField = AccessTools.FieldRefAccess<HotkeyComponent, HotKey>("hotkey");
                harmony.Patch(genTex,
                    prefix:    Ref(nameof(GlyphPatches.GenHotkeyTexture_Prefix)),
                    finalizer: Ref(nameof(GlyphPatches.GenHotkeyTexture_Finalizer)));
                session.Vtml.MarkActive();
            }

            // ── P4 · arte ──
            // Es el único canal que puede caerse sin llevarse nada: sin él, las
            // caras de PlayStation salen como texto ("Cross"), que es lo que hacían
            // hasta la etapa 3.
            if (drawHotkey is null || drawHotkey.ReturnType != typeof(double))
                Fail(session, ref artFailure, session.Art,
                     "HotkeyComponent.DrawHotkey(12 args) cambió de firma");
            else
            {
                harmony.Patch(drawHotkey, prefix: Ref(nameof(GlyphPatches.DrawHotkey_Prefix)));
                session.Art.MarkActive();
            }

            // ── P3 ──
            if (drawHelp is null || actualWidth is null || actualWidth.FieldType != typeof(double))
                Fail(session, ref signFailure, session.Sign,
                     "DrawWorldInteractionUtil.drawHelp(6 args)/ActualWidth cambiaron");
            else
            {
                harmony.Patch(drawHelp,
                    prefix:    Ref(nameof(GlyphPatches.DrawHelp_Prefix)),
                    postfix:   Ref(nameof(GlyphPatches.DrawHelp_Postfix)),
                    finalizer: Ref(nameof(GlyphPatches.DrawHelp_Finalizer)));
                session.Sign.MarkActive();
                WarnAboutForeignPatches(session, harmonyId, drawHelp, genTex);
            }

            SelfTest(capi, session);
        }
        catch (Exception e)
        {
            // Que esto NO escape es la regla más importante del archivo.
            capi.Logger.Warning(
                "GamepadCompanion: the glyph patches could not be applied; hints stay on the " +
                "keyboard and the rest of the mod works normally.");
            capi.Logger.Warning(e);
            Fail(session, ref rewriteFailure, session.Rewrite, e.GetType().Name);
        }
    }

    private static HarmonyMethod Ref(string name)
        => new(typeof(GlyphPatches).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!);

    private static void Fail(GlyphSession session, ref string? slot, GlyphChannel channel, string why)
    {
        slot = why;
        channel.MarkUnavailable(why);
    }

    // Las sesiones siguientes no reparchean, pero sus canales arrancan en
    // "no aplicado": hay que contarles cómo salió el único intento.
    private static void Adopt(GlyphSession session)
    {
        foreach (var (channel, failure) in new[]
                 {
                     (session.Rewrite, rewriteFailure),
                     (session.Vtml, vtmlFailure),
                     (session.Sign, signFailure),
                     (session.Art, artFailure),
                 })
        {
            if (failure is null) channel.MarkActive();
            else channel.MarkUnavailable(failure);
            channel.Note = sharedNote;
        }
    }

    // Lo único de la familia "otro mod nos pisa" que se puede saber sin ejecutar:
    // si alguien más tiene un prefix sobre nuestros targets. Un prefix ajeno que
    // devuelva false saltea el cuerpo del método y con él nuestra ventana.
    private static void WarnAboutForeignPatches(GlyphSession session, string ours,
                                                params MethodInfo?[] targets)
    {
        foreach (MethodInfo? target in targets)
        {
            if (target is null) continue;
            Patches? info = Harmony.GetPatchInfo(target);
            string[] others = info?.Prefixes
                                  .Select(p => p.owner)
                                  .Where(o => o != ours)
                                  .Distinct()
                                  .ToArray() ?? Array.Empty<string>();
            if (others.Length == 0) continue;

            sharedNote = $"otro mod tiene un prefix sobre {target.Name}: {string.Join(", ", others)}";
            session.Sign.Note = sharedNote;
            session.Vtml.Note = sharedNote;
            session.Logger.Notification("GamepadCompanion: {0}", sharedNote);
        }
    }

    // ── self-test ─────────────────────────────────────────────────────────────
    //
    // Atraviesa el CALL SITE REAL del engine, no una llamada nuestra. La
    // diferencia importa: el inlining es una decisión por call site y por método
    // llamador, así que comprobar que PrimaryAsString no está inlineado en un
    // método nuestro no dice absolutamente nada sobre si lo está adentro de
    // GenHotkeyTexture.
    //
    // Lo que SIGUE sin poder detectar, y hay que decirlo en voz alta en vez de
    // fingir que está cubierto: el inlining dentro de drawHelp (no lo ejercitamos)
    // y un re-JIT a tier1 que inlinee DESPUÉS de que el self-test pasó.
    private static void SelfTest(ICoreClientAPI capi, GlyphSession session)
    {
        const string probe = "GPCPROBE";
        if (!session.Rewrite.Usable) return;

        // Un solo try/finally para TODO: ProbeLabel hace que el resolver conteste
        // siempre lo mismo, así que dejarlo puesto envenena el mod entero — el
        // painter no puede meter "GPCPROBE" en una caja cuadrada, se rinde, y
        // desaparecen todos los glifos. Ya pasó: la primera versión lo limpiaba
        // en el finally del SEGUNDO bloque, y el primero salía por return.
        session.Resolver.ProbeLabel = probe;
        try
        {
            SelfTestBody(capi, session, probe);
        }
        finally
        {
            session.Resolver.ProbeLabel = null;
            GlyphScope.ResetForFrame();
        }
    }

    private static void SelfTestBody(ICoreClientAPI capi, GlyphSession session, string probe)
    {
        // (1) Directo: el postfix está puesto y la ventana lo gatea.
        try
        {
            var combination = new KeyCombination { KeyCode = (int)GlKeys.LShift };
            string outside = combination.PrimaryAsString();
            GlyphScope.Token token = GlyphScope.Enter(sign: false);
            string inside = combination.PrimaryAsString();
            GlyphScope.Exit(token);
            string identity = combination.ToString();

            if (inside != probe || outside == probe || identity.Contains(probe))
            {
                Fail(session, ref rewriteFailure, session.Rewrite,
                     $"self-test: dentro=({inside}) fuera=({outside}) identidad=({identity})");
                return;
            }
            session.Rewrite.MarkActive();
        }
        catch (Exception e)
        {
            Fail(session, ref rewriteFailure, session.Rewrite, "self-test: " + e.GetType().Name);
            return;
        }

        // (2) El call site de verdad: construir un HotkeyComponent y pedirle la
        //     textura es exactamente lo que hace el engine al componer un <hk>.
        //     Si esto tira, lo que falló es la SONDA (necesita Cairo y una textura
        //     GL), no el parche: se deja el canal activo y se anota, porque el peor
        //     modo de falla del parche es "no sustituye", que es inocuo.
        try
        {
            var component = new HotkeyComponent(capi, "secondarymouse", CairoFont.WhiteSmallText());
            try
            {
                component.GenHotkeyTexture();
                if (component.DisplayText?.Contains(probe) != true)
                    Fail(session, ref vtmlFailure, session.Vtml,
                         $"self-test del call site real: DisplayText=({component.DisplayText})");
            }
            finally { component.Dispose(); }
        }
        catch (Exception e)
        {
            sharedNote = "el self-test del call site real no se pudo correr (" + e.GetType().Name + ")";
            session.Vtml.Note = sharedNote;
            session.Logger.Notification("GamepadCompanion: {0}", sharedNote);
        }
    }
}
