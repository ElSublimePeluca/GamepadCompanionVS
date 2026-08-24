using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace GamepadCompanion.Toggles;

// Geometría del minimap HUD vanilla (GuiDialogWorldMap en modo HUD) para que
// ToggleHudOverlay pueda acomodarse debajo suyo en lugar de encimársele
// (issue #6).
//
// Detección por nombre de tipo, igual que WorldMapZoomMapper: GuiDialogWorldMap
// vive en VSEssentials y no vale la pena agregar la dependencia para leer un
// solo ElementBounds. Medimos el bound real del composer en vez de hardcodear
// el 250x250 del minimap, así el overlay sigue bien parado si vanilla cambia
// el tamaño o si otro mod recompone el dialog.
//
// El minimap se dibuja en la esquina que diga clientsettings
// (minimapHudPosition), así que solo hay conflicto cuando cae arriba a la
// derecha, que es donde vive el overlay. En cualquier otra esquina devolvemos
// null y el overlay se queda donde estaba siempre.
//
// Ojo con el mapa full-screen: NO es otro dialog, es el mismo instance que
// cambia de DialogType HUD a Dialog, así que mientras el usuario mira el mapa
// grande el minimap "desaparece". Sin memoria el overlay saltaría a la esquina
// y volvería abajo al cerrar el mapa; por eso mientras showMinimapHud siga
// prendido conservamos la última medición.
public sealed class MinimapAnchor
{
    private const string WorldMapDialogTypeName = "GuiDialogWorldMap";

    private readonly ICoreClientAPI capi;

    // Última medición del borde inferior, en unidades SIN escalar (las que
    // come ElementBounds.Fixed). NaN = nunca vimos el minimap arriba a la
    // derecha.
    private double lastBottomEdge = double.NaN;

    public MinimapAnchor(ICoreClientAPI capi)
    {
        this.capi = capi;
    }

    // Borde inferior del minimap en unidades sin escalar, o null si el minimap
    // no está ocupando la esquina superior derecha.
    public double? RightTopBottomEdge()
    {
        var bounds = FindHudMinimapBounds();

        if (bounds is not null)
        {
            if (bounds.Alignment != EnumDialogArea.RightTop)
            {
                lastBottomEdge = double.NaN;
                return null;
            }

            float scale = RuntimeEnv.GUIScale;
            if (scale <= 0f) scale = 1f;
            lastBottomEdge = (bounds.absY + bounds.OuterHeight) / scale;
            return lastBottomEdge;
        }

        // No hay minimap HUD abierto ahora mismo: o el usuario lo tiene
        // apagado (volvemos arriba) o está mirando el mapa full-screen (nos
        // quedamos donde estábamos hasta que vuelva).
        if (double.IsNaN(lastBottomEdge)) return null;
        if (!capi.Settings.Bool.Get("showMinimapHud", false))
        {
            lastBottomEdge = double.NaN;
            return null;
        }
        return lastBottomEdge;
    }

    private ElementBounds? FindHudMinimapBounds()
    {
        foreach (var dlg in capi.Gui.OpenedGuis)
        {
            if (dlg is null || !dlg.IsOpened()) continue;
            if (dlg.DialogType != EnumDialogType.HUD) continue;
            if (dlg.GetType().Name != WorldMapDialogTypeName) continue;

            var bounds = dlg.SingleComposer?.Bounds;
            // Antes del primer Compose los abs* son basura; ignoramos hasta
            // que el engine los haya calculado.
            if (bounds is null || !bounds.Initialized) continue;
            return bounds;
        }
        return null;
    }
}
