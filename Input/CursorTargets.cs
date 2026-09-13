using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Vintagestory.API.Client;

namespace GamepadCompanion.Input;

// Junta los destinos del D-pad: los rectángulos que el jugador reconoce como "un
// lugar donde apretar" en los diálogos abiertos. Cuál se elige lo decide
// CursorNavigation, que vive aparte para poder probarlo sin el juego.
//
// Lista blanca por tipo y no "todo lo interactivo": entre los elementos
// interactivos también están la barra de título (se arrastra), los scrollbars y
// los sliders (un click en el centro CAMBIA el valor), los textos flotantes y el
// mapa entero. Lo que no está acá se alcanza con el stick derecho, que es la red
// de seguridad para cualquier GUI, incluidas las de mods con elementos propios.
//
// De los HUD sólo se toman slots: así entra el hotbar, que con el inventario
// abierto se clickea, y quedan afuera el campo del chat y el minimapa, que están
// "abiertos" todo el tiempo.
//
// Tres datos salen por reflection porque la API no los expone. Si un update los
// renombra, ese tipo de destino deja de aparecer y nada más (el stick sigue
// andando), y `gpclab apicheck` lo avisa antes:
//   - GuiComposer.interactiveElements (internal): los elementos de cada composer.
//   - GuiElementSkillItemGrid.skillItems (private): cuántas celdas tienen receta.
//   - GuiElementHorizontalTabs.tabWidths y currentScrollOffset (private): dónde
//     empieza cada pestaña.
internal static class CursorTargets
{
    private const BindingFlags AnyInstance =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly FieldInfo? InteractiveElements =
        typeof(GuiComposer).GetField("interactiveElements", AnyInstance);
    private static readonly FieldInfo? SkillItems =
        typeof(GuiElementSkillItemGrid).GetField("skillItems", AnyInstance);
    private static readonly FieldInfo? TabWidths =
        typeof(GuiElementHorizontalTabs).GetField("tabWidths", AnyInstance);
    private static readonly FieldInfo? TabScroll =
        typeof(GuiElementHorizontalTabs).GetField("currentScrollOffset", AnyInstance);

    // Devuelve cuántos de los destinos salieron de diálogos y no de HUDs. Con cero,
    // el diálogo abierto no tiene nada que el D-pad reconozca (un menú de ImGui,
    // una GUI dibujada a mano) y los slots del hotbar no alcanzan para recorrerlo:
    // CursorNavigator usa el paso fijo.
    public static int Collect(ICoreClientAPI capi, List<NavRect> into)
    {
        into.Clear();
        if (InteractiveElements is null) return 0;

        int fromDialogs = 0;
        foreach (GuiDialog dialog in capi.Gui.OpenedGuis)
        {
            if (dialog is null || !dialog.IsOpened() || !dialog.ShouldReceiveMouseEvents()) continue;
            if (dialog.Composers is null) continue;
            bool slotsOnly = dialog.DialogType != EnumDialogType.Dialog;
            int before = into.Count;

            // Composers y no SingleComposer: hay diálogos con varios a la vez, y el
            // inventario cambia cuál tiene adentro según el modo de juego, así que
            // lo que está ahí es exactamente lo que se dibuja.
            foreach (GuiComposer composer in dialog.Composers.Values)
            {
                if (composer is null || !composer.Enabled) continue;
                if (InteractiveElements.GetValue(composer) is not Dictionary<string, GuiElement> elements)
                    continue;
                foreach (GuiElement element in elements.Values)
                    Add(element, slotsOnly, into);
            }

            if (!slotsOnly) fromDialogs += into.Count - before;
        }
        return fromDialogs;
    }

    private static void Add(GuiElement element, bool slotsOnly, List<NavRect> into)
    {
        switch (element)
        {
            case GuiElementItemSlotGridBase grid:
                AddSlots(grid, into);
                break;
            case GuiElementPassiveItemSlot:
                AddWhole(element, into);
                break;
            case GuiElementSkillItemGrid skills when !slotsOnly:
                AddSkillCells(skills, into);
                break;
            case GuiElementHorizontalTabs strip when !slotsOnly:
                AddTabs(strip, into);
                break;
            case GuiElementTextButton or GuiElementToggleButton or GuiElementSwitch
                 or GuiElementDropDown or GuiElementEditableTextBase when !slotsOnly:
                if (element is GuiElementControl { Enabled: false }) break;
                AddWhole(element, into);
                break;
        }
    }

    private static void AddSlots(GuiElementItemSlotGridBase grid, List<NavRect> into)
    {
        ElementBounds[]? slots = grid.SlotBounds;
        if (slots is null) return;
        ElementBounds? parent = grid.Bounds?.ParentBounds;
        foreach (ElementBounds? slot in slots)
        {
            if (slot is null) continue;
            var rect = new NavRect(slot.absX, slot.absY, slot.OuterWidth, slot.OuterHeight);
            // El hover del engine exige además estar adentro del padre de la
            // grilla, que en los inventarios con scroll es el recorte: un slot
            // scrolleado fuera de vista existe, pero no se puede apuntar.
            if (parent is not null && !parent.PointInside(rect.CenterX, rect.CenterY)) continue;
            if (InsideClip(grid, rect)) into.Add(rect);
        }
    }

    private static void AddWhole(GuiElement element, List<NavRect> into)
    {
        ElementBounds? b = element.Bounds;
        if (b is null || b.OuterWidth <= 0 || b.OuterHeight <= 0) return;
        var rect = new NavRect(b.absX, b.absY, b.OuterWidth, b.OuterHeight);
        if (InsideClip(element, rect)) into.Add(rect);
    }

    // Las celdas del selector de recetas (knapping, arcilla, yunque) y del modo de
    // herramienta. El engine no guarda sus rectángulos: los recalcula en cada frame
    // con la misma cuenta que acá (GuiElementSkillItemGrid.RenderInteractiveElements).
    private static void AddSkillCells(GuiElementSkillItemGrid grid, List<NavRect> into)
    {
        double unscaledPitch = GuiElementPassiveItemSlot.unscaledSlotSize
                             + GuiElementItemSlotGridBase.unscaledSlotPadding;
        ElementBounds b = grid.Bounds;
        // El constructor fija el tamaño en celdas × paso, así que columnas y filas
        // se leen de ahí sin tocar los privados.
        int cols = (int)Math.Round(b.fixedWidth / unscaledPitch);
        int rows = (int)Math.Round(b.fixedHeight / unscaledPitch);
        int count = cols * rows;
        // Las celdas vacías de la última fila no hacen nada al clickearlas.
        if (SkillItems?.GetValue(grid) is ICollection items) count = Math.Min(count, items.Count);

        double pitch = GuiElement.scaled(unscaledPitch);
        double size  = GuiElement.scaled(GuiElementPassiveItemSlot.unscaledSlotSize);
        for (int i = 0; i < count; i++)
        {
            var rect = new NavRect(b.absX + (i % cols) * pitch, b.absY + (i / cols) * pitch,
                                   size, size);
            if (InsideClip(grid, rect)) into.Add(rect);
        }
    }

    // Cada pestaña es un destino. Se reconstruye con la misma cuenta del click en
    // GuiElementHorizontalTabs.OnMouseDownOnElement: la primera empieza a un
    // espaciado del borde, cada una avanza su ancho más un espaciado, y todo se
    // corre con el scroll de la barra.
    private static void AddTabs(GuiElementHorizontalTabs strip, List<NavRect> into)
    {
        if (TabWidths?.GetValue(strip) is not int[] widths || strip.tabs is null) return;
        double scroll = TabScroll?.GetValue(strip) is double s ? s : 0;
        double spacing = GuiElement.scaled(strip.unscaledTabSpacing);
        ElementBounds b = strip.Bounds;

        double x = spacing;
        int count = Math.Min(strip.tabs.Length, widths.Length);
        for (int i = 0; i < count; i++)
        {
            var rect = new NavRect(b.absX + x - scroll, b.absY, widths[i], b.InnerHeight);
            x += widths[i] + spacing;
            // Con flechas de scroll, las pestañas corridas fuera de la barra no se
            // pueden clickear.
            if (rect.CenterX < b.absX || rect.CenterX > b.absX + b.InnerWidth) continue;
            if (InsideClip(strip, rect)) into.Add(rect);
        }
    }

    private static bool InsideClip(GuiElement element, NavRect rect)
        => element.InsideClipBounds is not { } clip || clip.PointInside(rect.CenterX, rect.CenterY);
}
