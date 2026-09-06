namespace GamepadCompanion.Glyphs;

// De dónde salió la afirmación "esta tecla la hace este botón". El ORDEN DE LOS
// MIEMBROS ES LA PRIORIDAD: el primero gana cuando dos fuentes reclaman el mismo
// keycode.
//
// UserButton por encima de Toggle es una decisión, no un accidente. Si alguien
// se bindeó a mano un botón a "mantener Shift" (el caso de compatibilidad con
// RKN Crafting), el hint tiene que mostrar ESE botón y no el R3 hardcodeado: la
// configuración explícita del usuario le gana al default del mod. Además el
// binding a mano es un hold de verdad, así que su etiqueta no necesita la marca
// de toggle y es más honesta.
internal enum GlyphSource
{
    Trigger,
    UserButton,
    Movement,
    Toggle,
    DefaultButton,
    Wheel,
}

// Una entrada del mapa inverso. Label ya viene formateada para la familia
// activa, con la marca de toggle si corresponde.
internal readonly record struct GlyphBinding(
    string Label,
    GamepadInput Input,
    GlyphSource Source)
{
    public int Priority => (int)Source;
}
