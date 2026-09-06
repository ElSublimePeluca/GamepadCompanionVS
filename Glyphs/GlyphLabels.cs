namespace GamepadCompanion.Glyphs;

// La tabla (control, familia) → etiqueta corta.
//
// Tres reglas que no son negociables, todas medidas o ya documentadas en el repo:
//
//   1. NADA de flechas Unicode ni de ✕ ○ □ △. Cairo los dibuja como la caja del
//      glifo faltante — 12 px de rectángulo vacío. Ya está anotado en
//      Gui/ConfigDialog.cs por el mismo motivo. Los símbolos reales de
//      PlayStation llegan como arte (etapa E4), no como texto.
//   2. NADA se traduce. Son las letras impresas en el plástico: un LB es "LB"
//      en cualquier idioma, y la prensa en castellano las escribe igual.
//   3. NADA de abreviaturas inventadas tipo "Cua"/"Tri". Ilegibles en un juego
//      cuyo idioma por default es inglés.
internal static class GlyphLabels
{
    // Marca de "esto es un toggle, no se mantiene". L3 y R3 en este mod NO se
    // mantienen apretados: se togglean (Toggles/ToggleManager.cs). Sin la marca,
    // un cartel que dice "[RS] + [LT]" se lee como "mantené RS y apretá LT",
    // cuando la secuencia real es apretar RS, apretar LT, apretar RS de nuevo.
    // Alcanza al 28 % de los tags <hk> del juego y a la mayoría de las líneas
    // del cartel con modificador, así que mentir ahí no es un detalle.
    // El README explica la convención.
    public const string ToggleMark = "*";

    public static string Label(GamepadInput input, GlyphStyle style, bool isToggle = false)
        => Plain(input, style) + (isToggle ? ToggleMark : "");

    private static string Plain(GamepadInput input, GlyphStyle style) => style switch
    {
        GlyphStyle.PlayStation => input switch
        {
            GamepadInput.FaceSouth       => "Cross",
            GamepadInput.FaceEast        => "Circle",
            GamepadInput.FaceWest        => "Square",
            GamepadInput.FaceNorth       => "Triangle",
            GamepadInput.ShoulderLeft    => "L1",
            GamepadInput.ShoulderRight   => "R1",
            GamepadInput.TriggerLeft     => "L2",
            GamepadInput.TriggerRight    => "R2",
            GamepadInput.StickLeftPress  => "L3",
            GamepadInput.StickRightPress => "R3",
            GamepadInput.Back            => "Share",
            GamepadInput.Start           => "Options",
            _                            => DPad(input),
        },
        GlyphStyle.Nintendo => input switch
        {
            // A y B están cambiados de lugar respecto de Xbox, igual que X e Y.
            GamepadInput.FaceSouth       => "B",
            GamepadInput.FaceEast        => "A",
            GamepadInput.FaceWest        => "Y",
            GamepadInput.FaceNorth       => "X",
            GamepadInput.ShoulderLeft    => "L",
            GamepadInput.ShoulderRight   => "R",
            GamepadInput.TriggerLeft     => "ZL",
            GamepadInput.TriggerRight    => "ZR",
            GamepadInput.StickLeftPress  => "L3",
            GamepadInput.StickRightPress => "R3",
            GamepadInput.Back            => "-",
            GamepadInput.Start           => "+",
            _                            => DPad(input),
        },
        // Xbox es también el default de cualquier mando desconocido y el de
        // Steam Deck / Steam Controller, que usan serigrafía ABXY.
        _ => input switch
        {
            GamepadInput.FaceSouth       => "A",
            GamepadInput.FaceEast        => "B",
            GamepadInput.FaceWest        => "X",
            GamepadInput.FaceNorth       => "Y",
            GamepadInput.ShoulderLeft    => "LB",
            GamepadInput.ShoulderRight   => "RB",
            GamepadInput.TriggerLeft     => "LT",
            GamepadInput.TriggerRight    => "RT",
            GamepadInput.StickLeftPress  => "LS",
            GamepadInput.StickRightPress => "RS",
            GamepadInput.Back            => "View",
            GamepadInput.Start           => "Menu",
            _                            => DPad(input),
        },
    };

    // El D-pad se escribe igual en las tres familias.
    private static string DPad(GamepadInput input) => input switch
    {
        GamepadInput.DPadUp    => "D-Up",
        GamepadInput.DPadDown  => "D-Down",
        GamepadInput.DPadLeft  => "D-Left",
        GamepadInput.DPadRight => "D-Right",
        _                      => "?",
    };
}
