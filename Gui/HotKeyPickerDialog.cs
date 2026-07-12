using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace GamepadCompanion.Gui;

// Sub-dialog para elegir una entrada (hotkey/dialog toggle) de una lista
// filtrable. Llama al callback con el índice elegido (en el array de
// entries pasado al constructor) o 0 para "ninguno". Si el usuario cierra
// sin elegir, el callback no se invoca.
//
// Diseño con dos composers para evitar recrear el text input al cambiar el
// filtro (eso disparaba un loop SetValue→OnTextChanged que crasheaba el
// juego con OOM):
//   - "frame": BG + title + search input + footer. Estático, una vez.
//   - "list":  items filtrados. Se recompone en cada keystroke.
// La lista usa BeginClip+AddCompactVerticalScrollbar para scrollear el
// contenido completo dentro de una ventana fija; sin scroll, dialogs con
// muchos hotkeys (gameplay+todos los mods) requerían tipear sí o sí para
// encontrar entries no visibles.
public sealed class HotKeyPickerDialog : GuiDialog
{
    private const int    VisibleRows   = 18;
    private const double DialogW       = 460;
    private const double TitleH        = 30;
    private const double Margin        = 16;
    private const double SearchH       = 30;
    private const double RowH          = 24;
    private const double RowGap        = 2;
    private const double FooterH       = 36;
    private const double FooterGap     = 12;
    private const double ScrollbarW    = 20;

    private static readonly double ListAreaY    = TitleH + Margin + SearchH + Margin;
    private static readonly double ListAreaH    = VisibleRows * (RowH + RowGap);
    private static readonly double FooterY      = ListAreaY + ListAreaH + FooterGap;
    private static readonly double TotalH       = FooterY + FooterH + Margin;

    private readonly string title;
    private readonly IReadOnlyList<string> entryNames;
    private readonly Action<int> onPick;
    private readonly int currentSelectedIndex;
    private readonly string clearButtonLabel;

    private string filter = "";
    private List<int> filteredIndices = new();
    private int scrollOffset = 0;

    public override string ToggleKeyCombinationCode => null!;
    public override double DrawOrder => 0.6;

    public HotKeyPickerDialog(
        ICoreClientAPI capi,
        string title,
        IReadOnlyList<string> entryNames,
        int currentSelectedIndex,
        Action<int> onPick,
        string? clearButtonLabel = null) : base(capi)
    {
        this.title = title;
        this.entryNames = entryNames;
        this.currentSelectedIndex = currentSelectedIndex;
        this.onPick = onPick;
        this.clearButtonLabel = clearButtonLabel
            ?? Lang.Get("gamepadcompanion:clear-binding");
        RecomputeFiltered();
        ComposeFrame();
        ComposeList();
    }

    private void RecomputeFiltered()
    {
        filteredIndices.Clear();
        // Index 0 ("ninguno") se ofrece como botón "Quitar binding" en el
        // footer, no aparece en la lista.
        for (int i = 1; i < entryNames.Count; i++)
        {
            if (string.IsNullOrEmpty(filter) ||
                entryNames[i].Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                filteredIndices.Add(i);
            }
        }
    }

    private ElementBounds NewDialogBounds() =>
        ElementStdBounds.AutosizedMainDialog
            .WithAlignment(EnumDialogArea.CenterMiddle);

    private void ComposeFrame()
    {
        var dialogBounds = NewDialogBounds();
        var bgBounds = ElementBounds.Fixed(0, 0, DialogW, TotalH);
        bgBounds.BothSizing = ElementSizing.FitToChildren;

        var titleBarBounds = ElementBounds.Fixed(0, 0, DialogW, TitleH);
        var searchBounds = ElementBounds.Fixed(
            Margin, TitleH + Margin, DialogW - 2 * Margin, SearchH);

        // Scrollbar vive en frame (no se recompone) para preservar el
        // drag state cuando ComposeList recrea los items en cada scroll.
        var scrollbarBounds = ElementBounds.Fixed(
            Margin + (DialogW - 2 * Margin - ScrollbarW - 6) + 6,
            ListAreaY, ScrollbarW, ListAreaH);

        double btnW = (DialogW - 2 * Margin - FooterGap) / 2;
        var clearBounds  = ElementBounds.Fixed(Margin, FooterY, btnW, FooterH);
        var cancelBounds = ElementBounds.Fixed(
            Margin + btnW + FooterGap, FooterY, btnW, FooterH);

        Composers["frame"] = capi.Gui
            .CreateCompo("gpcompanion-picker-frame", dialogBounds)
            .AddShadedDialogBG(bgBounds)
            .AddDialogTitleBar(title, OnCancel, bounds: titleBarBounds)
            .BeginChildElements(bgBounds)
            .AddTextInput(searchBounds, OnSearchChanged,
                          CairoFont.WhiteSmallText(), "search")
            .AddCompactVerticalScrollbar(OnScroll, scrollbarBounds, "scrollbar")
            .AddSmallButton(clearButtonLabel, OnClear, clearBounds)
            .AddSmallButton(Lang.Get("gamepadcompanion:cancel"),
                            () => { OnCancel(); return true; },
                            cancelBounds)
            .EndChildElements()
            .Compose();

        // Focus inicial al search para que el usuario pueda typear sin click.
        var input = Composers["frame"].GetTextInput("search");
        Composers["frame"].FocusElement(input.TabIndex);

        UpdateScrollbarHeights();
    }

    private void UpdateScrollbarHeights()
    {
        double itemRowH = RowH + RowGap;
        double visiblePx = VisibleRows * itemRowH;
        double totalPx   = Math.Max(visiblePx, filteredIndices.Count * itemRowH);
        Composers["frame"].GetScrollbar("scrollbar")
            .SetHeights((float)visiblePx, (float)totalPx);
    }

    private void ComposeList()
    {
        var dialogBounds = NewDialogBounds();
        var containerBounds = ElementBounds.Fixed(0, 0, DialogW, TotalH);
        containerBounds.BothSizing = ElementSizing.Fixed;

        // Solo items en posiciones absolutas. Mostramos VisibleRows items
        // a partir de scrollOffset; el scrollbar (que vive en frame)
        // ajusta scrollOffset via OnScroll y disparamos solo este
        // ComposeList — frame, search input y scrollbar permanecen vivos.
        double listW    = DialogW - 2 * Margin - ScrollbarW - 6;
        double itemRowH = RowH + RowGap;

        int total = filteredIndices.Count;
        int visible = Math.Min(VisibleRows, total);
        int maxOffset = Math.Max(0, total - VisibleRows);
        if (scrollOffset > maxOffset) scrollOffset = maxOffset;
        if (scrollOffset < 0) scrollOffset = 0;

        var compo = capi.Gui
            .CreateCompo("gpcompanion-picker-list", dialogBounds)
            .BeginChildElements(containerBounds);

        for (int row = 0; row < visible; row++)
        {
            int idx = filteredIndices[scrollOffset + row];
            var bounds = ElementBounds.Fixed(
                Margin, ListAreaY + row * itemRowH, listW, RowH);
            string label = entryNames[idx];
            if (idx == currentSelectedIndex) label = "▸ " + label;
            compo.AddSmallButton(label, () => OnPicked(idx),
                                 bounds, EnumButtonStyle.Small);
        }

        if (total == 0 && !string.IsNullOrEmpty(filter))
        {
            var bounds = ElementBounds.Fixed(
                Margin, ListAreaY, listW, RowH);
            compo.AddStaticText(
                Lang.Get("gamepadcompanion:no-results"),
                CairoFont.WhiteDetailText(), bounds);
        }

        compo.EndChildElements();

        Composers["list"] = compo.Compose();
    }

    private void OnScroll(float value)
    {
        double itemRowH = RowH + RowGap;
        int newOffset = (int)Math.Round(value / itemRowH);
        if (newOffset == scrollOffset) return;
        scrollOffset = newOffset;
        ComposeList();
    }

    private void OnSearchChanged(string text)
    {
        filter = text ?? "";
        scrollOffset = 0;     // reset al filtrar — la lista cambia de tamaño
        RecomputeFiltered();
        // Solo recomponemos la lista. El frame (con el text input vivo
        // y el scrollbar) queda intacto. Pero el rango del scrollbar
        // cambió porque hay distinta cantidad de items: actualizamos
        // SetHeights y el thumb position sin recomponer.
        UpdateScrollbarHeights();
        var sb = Composers["frame"].GetScrollbar("scrollbar");
        sb.CurrentYPosition = 0;
        ComposeList();
    }

    private bool OnPicked(int index)
    {
        onPick(index);
        TryClose();
        return true;
    }

    private bool OnClear()
    {
        onPick(0);
        TryClose();
        return true;
    }

    private void OnCancel() => TryClose();
}
