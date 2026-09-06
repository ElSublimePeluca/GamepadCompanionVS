namespace GamepadCompanion.Glyphs;

using System;
using System.Runtime.CompilerServices;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;

// Las texturas de la GUI se hornean una vez y se reusan, así que cambiar el mapa
// inverso no se ve hasta que algo pida recomponer. Y el cartel del bloque mirado
// NO se recompone solo: sólo lo hace si cambia el bloque, la posición o la cara
// mirada. Mirando fijo un cofre y desenchufando el mando, sin esto, el cartel se
// queda con los glifos para siempre. Es exactamente el síntoma del issue, así
// que la invalidación explícita no es un extra.
internal sealed class GlyphInvalidator
{
    // "Restaurar valores por defecto" en Ajustes > Controles dispara el watcher
    // una vez POR HOTKEY (~150 veces). Invalidar el mapa es barato; recomponer
    // 150 veces sería un tirón bien visible. De ahí el debounce.
    private const int DebounceMs = 250;

    private readonly ICoreClientAPI capi;
    private readonly GlyphSession session;
    private bool pending;

    public GlyphInvalidator(ICoreClientAPI capi, GlyphSession session)
    {
        this.capi = capi;
        this.session = session;
    }

    public void Wire()
    {
        // Los dos watchers se limpian al terminar la sesión de mundo
        // (ClientMain llama a ClientSettings.Inst.ClearWatchers en el teardown),
        // así que se re-suscriben en cada StartClientSide, igual que CustomIcons.
        ClientSettings.Inst.AddKeyCombinationUpdatedWatcher((_, _) => session.Resolver.Invalidate());
        capi.Event.HotkeysChanged += session.Resolver.Invalidate;

        session.Resolver.Changed += () => pending = true;
        capi.Event.RegisterGameTickListener(_ => Flush(), DebounceMs);
    }

    // Pedir una recomposición sin invalidar el mapa inverso: lo usan los parches
    // cuando degradan una línea concreta y hay que volver a dibujar el cartel,
    // pero el mapa no cambió.
    public void RequestRefresh() => pending = true;

    private void Flush()
    {
        if (!pending) return;
        pending = false;
        try
        {
            MarkAllForRecompose(capi);
            RefreshItemTooltips(capi);
        }
        catch (Exception e)
        {
            session.Icons.ReportFailure(e);
        }
    }

    // El tooltip de ítem es el único elemento con tags <hk> que una recomposición
    // NO alcanza: su ComposeElements está vacío y el texto se arma en
    // SetSourceSlot. Por suerte curSlot es público y SetSourceSlot tiene un
    // forceRecompose también público, así que no hace falta reflexión ni pasarle
    // null (que haría desaparecer el tooltip por un frame).
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RefreshItemTooltips(ICoreClientAPI capi)
    {
        foreach (GuiDialog dialog in capi.Gui.LoadedGuis)
        {
            if (dialog?.Composers is null) continue;
            foreach (GuiComposer composer in dialog.Composers.Values)
            {
                // Los dos nombres que usa vanilla; no hay forma de enumerarlos.
                for (int i = 1; i <= 2; i++)
                    if (composer?.GetElement("itemstackinfo" + i) is GuiElementItemstackInfo info
                        && info.curSlot is not null)
                        info.SetSourceSlot(info.curSlot, forceRecompose: true);
            }
        }
    }

    // Sin inlining, y no como regla general sino acá por un motivo puntual: el
    // CLR resuelve los tokens de método y campo al jitear el método que los
    // CONTIENE. Si `ClientMain.GuiComposers` o `MarkAllDialogsForRecompose`
    // desaparecieran en un update y esto estuviera inlineado en el llamador, la
    // MissingMethodException saldría ANTES de entrar al try, en el call site del
    // llamador — que en este caso es un tick listener del engine.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void MarkAllForRecompose(ICoreClientAPI capi)
    {
        // El manager de la PARTIDA. ScreenManager.GuiComposers es el del MENÚ
        // PRINCIPAL y no refresca nada in-game; por eso la hotkey de debug
        // `recomposeallguis` tampoco sirve para esto.
        //
        // Es diferido: MarkAllDialogsForRecompose sólo marca una bandera que
        // GuiComposer.Render consume ANTES de dibujar, así que no hay un frame en
        // blanco. RecomposeAllDialogs() sería síncrono y loguearía un renglón por
        // diálogo, cada 250 ms.
        if (capi.World is ClientMain client) client.GuiComposers.MarkAllDialogsForRecompose();
    }
}
