using System;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace GamepadCompanion.Gui;

// Los botones de VS se agrandan solos para que el label entre en una línea:
// GuiComposer.Compose() corre GuiElementTextButton.BeforeCalcBounds() sobre
// todos los elementos antes de calcular bounds, y ese hook hace
// CairoFont.AutoBoxSize(onlyGrow: true), o sea
// fixedWidth = max(fixedWidth, anchoDelTexto / GUIScale + 1).
//
// El botón nunca se achica y nadie lo clippea (se dibuja de su propia textura,
// no del surface del composer), así que un label largo se renderiza fuera del
// panel, flotando sobre el mundo. Los labels del mod no tienen cota: salen de
// nombres de hotkeys de otros mods y de CompositeAction, que une hasta 4
// acciones con " + " (medido: dos pasos ya dan 425px contra los 386 que hay
// hasta el borde del panel en la tab Rueda). Así que recortamos nosotros
// antes de mandarlos al composer.
public static class GuiTextFit
{
    private const string Ellipsis = "...";

    // Recorta el label para que el botón no crezca más allá de maxWidth, en
    // unidades sin escalar (las mismas que come ElementBounds.Fixed).
    public static string EllipsizeButton(string label, double maxWidth,
                                         EnumButtonStyle style = EnumButtonStyle.Normal)
        => Ellipsize(label, maxWidth, CairoFont.SmallButtonText(style));

    public static string Ellipsize(string label, double maxWidth, CairoFont font)
    {
        if (string.IsNullOrEmpty(label)) return label;

        // AutoBoxSize le suma 1px al ancho medido, así que ese es el margen
        // que hay que dejar para que el botón se quede en maxWidth.
        double budget = maxWidth - 1;
        if (budget <= 0) return "";
        if (Width(font, label) <= budget) return label;

        // Prefijo más largo que entre con los puntos suspensivos.
        int lo = 0, hi = label.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (Width(font, label[..mid] + Ellipsis) <= budget) lo = mid;
            else hi = mid - 1;
        }

        // No partir un par subrogado al medio.
        if (lo > 0 && char.IsHighSurrogate(label[lo - 1])) lo--;
        if (lo <= 0) return Ellipsis;
        return label[..lo].TrimEnd() + Ellipsis;
    }

    private static double Width(CairoFont font, string text)
    {
        float scale = RuntimeEnv.GUIScale;
        if (scale <= 0f) scale = 1f;
        return font.GetTextExtents(text).Width / scale;
    }
}
