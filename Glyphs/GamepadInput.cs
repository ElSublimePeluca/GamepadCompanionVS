namespace GamepadCompanion.Glyphs;

using GamepadCompanion.Input;

// Nombre POSICIONAL de cada control del mando, independiente de la serigrafía.
// El botón de abajo del rombo es FaceSouth siempre: en un Xbox dice "A", en un
// DualSense dice "✕" y en un Pro Controller dice "B". Esa última fila es
// justamente por qué el enum no puede llamarse A/B/X/Y — en Nintendo A y B
// están cambiados de lugar respecto de Xbox, así que un mapa indexado por letra
// se equivoca en la mitad de los casos.
//
// GamepadButton (Input/GamepadButton.cs) es el enum del driver y sí usa las
// letras de Xbox, pero sus valores son posiciones de botón de GLFW. La
// traducción entre los dos vive en GlyphInputs.FromButton y es la única.
public enum GamepadInput
{
    FaceSouth,
    FaceEast,
    FaceWest,
    FaceNorth,
    ShoulderLeft,
    ShoulderRight,
    TriggerLeft,
    TriggerRight,
    StickLeftPress,
    StickRightPress,
    Back,
    Start,
    DPadUp,
    DPadDown,
    DPadLeft,
    DPadRight,
}

internal static class GlyphInputs
{
    // null = ese botón no tiene glifo que mostrar. Hoy sólo Guide, que el mod
    // no usa para nada y que en muchos mandos ni siquiera llega a GLFW.
    public static GamepadInput? FromButton(GamepadButton button) => button switch
    {
        GamepadButton.A            => GamepadInput.FaceSouth,
        GamepadButton.B            => GamepadInput.FaceEast,
        GamepadButton.X            => GamepadInput.FaceWest,
        GamepadButton.Y            => GamepadInput.FaceNorth,
        GamepadButton.LeftBumper   => GamepadInput.ShoulderLeft,
        GamepadButton.RightBumper  => GamepadInput.ShoulderRight,
        GamepadButton.Back         => GamepadInput.Back,
        GamepadButton.Start        => GamepadInput.Start,
        GamepadButton.LeftStick    => GamepadInput.StickLeftPress,
        GamepadButton.RightStick   => GamepadInput.StickRightPress,
        GamepadButton.DPadUp       => GamepadInput.DPadUp,
        GamepadButton.DPadDown     => GamepadInput.DPadDown,
        GamepadButton.DPadLeft     => GamepadInput.DPadLeft,
        GamepadButton.DPadRight    => GamepadInput.DPadRight,
        _                          => null,
    };
}
