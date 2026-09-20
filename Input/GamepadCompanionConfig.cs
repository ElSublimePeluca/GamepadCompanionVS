using System.Collections.Generic;

namespace GamepadCompanion.Input;

// Config persistido en ~/.config/VintagestoryData/ModConfig/gamepadcompanion.json
// vía ICoreAPI.LoadModConfig / StoreModConfig.
public sealed class GamepadCompanionConfig
{
    public float YawSensitivity   { get; set; } = 1500f;
    public float PitchSensitivity { get; set; } = 1100f;
    public bool  InvertPitch      { get; set; } = true;
    public float Deadzone         { get; set; } = 0.15f;

    // Multiplicador de sensibilidad cuando el modo precisión está activo
    // (DPad ↑ toggle). 0.3 = 30% del speed normal, útil para apuntar
    // bloques específicos o entidades a distancia sin overshoot.
    public float PrecisionFactor  { get; set; } = 0.3f;

    // Intercambia LT ↔ RT antes de que los mappers los lean. El default (RT =
    // atacar/romper = click izq, LT = interactuar = click der) es intencional
    // (ver TriggerMapper), pero algunos controles reportan los ejes al revés o
    // el usuario prefiere el reflejo invertido. Afecta triggers in-world y
    // clicks del cursor virtual por igual.
    public bool  SwapTriggers     { get; set; } = false;

    // Device fijado a mano con .gpdevice: substring del nombre que reporta
    // GLFW (case-insensitive). null = autodetección. Sirve cuando hay más de
    // un joystick enchufado y el mod elige el que no es — típicamente algún
    // HID que no es un gamepad pero tiene forma de gamepad (controladoras RGB
    // de placa madre, teclados con teclas programables).
    public string? PreferredDevice { get; set; } = null;

    // Slots de la rueda radial. null en el array entero significa "usar
    // defaults"; null en una posición significa "slot vacío". Length 8
    // cuando está poblado. La forma del SlotConfig es plana con
    // discriminador para evitar custom JsonConverter.
    public SlotConfig?[]? RadialSlots { get; set; } = null;

    // Familia de glifos para los hints del juego: "off" | "auto" | "xbox" |
    // "playstation" | "nintendo". Es un STRING y no un enum a propósito:
    // LoadModConfig deserializa sin tolerancia y un enum con un valor
    // desconocido tira dentro de StartClientSide. El parseo vive en
    // Glyphs/GlyphStyle.cs y cae en "auto" ante cualquier cosa rara.
    public string GlyphStyle { get; set; } = "auto";

    // Si las acciones asignadas a la rueda radial también muestran glifo. Se
    // muestra "LB" a secas (mantené LB, está en la rueda): el índice del slot
    // no lo ve el jugador, así que "LB+3" no sería accionable.
    public bool GlyphWheel { get; set; } = true;

    // Canales de glifos apagados a mano, separados por coma: "icons,sign".
    // Escotilla de emergencia para un reporte, sin tener que tocar código.
    public string? GlyphChannelsOff { get; set; } = null;

    // Override de la acción edge-press de un botón. Key = nombre del
    // GamepadButton (A, B, DPadLeft, etc.). Una entry presente reemplaza el
    // default del LAYOUT para ese botón; null/ausente = usar el default.
    // El botón que el layout reserva para la rueda ignora las dos cosas.
    public Dictionary<string, SlotConfig?>? ButtonBindings { get; set; } = null;

    // Preset de defaults de botones: "classic" (1.13 y anteriores) o "modern".
    // STRING y no enum por el mismo motivo que GlyphStyle: LoadModConfig
    // deserializa sin tolerancia y un valor desconocido tiraría DENTRO de
    // StartClientSide, llevándose el mod entero. El parseo vive en
    // Input/GamepadLayout.cs y cae en el default ante cualquier cosa rara.
    //
    // **null significa "todavía no lo eligió", y eso NO es lo mismo que el
    // default.** Si ya había archivo de config, el mod se queda con el clásico
    // —que es exactamente lo que el usuario tenía— y pregunta una vez; en una
    // instalación nueva arranca con el nuevo y no pregunta nada. Ver
    // GamepadCompanionModSystem.ResolveLayout.
    public string? Layout { get; set; } = null;
}

// Representación serializable de un IGameAction. `Type` discrimina:
//   "hotkey"     → HotKeyAction(Code, Label)
//   "openDialog" → OpenLoadedGuiAction(DialogType, Label)
//   "builtin"    → BuiltinAction(Code, Label)
//   "keypress"   → KeyPressAction(KeyCode, Ctrl/Shift/AltPressed, Label)
//   "composite"  → CompositeAction(Children=[SlotConfig...], Label)
// Campos no relevantes para el tipo se ignoran. No anidamos composites
// (la deserialización ignora hijos que sean composite a su vez).
public sealed class SlotConfig
{
    public string? Type       { get; set; }
    public string? Code       { get; set; }
    public string? DialogType { get; set; }
    public string? Label      { get; set; }
    public SlotConfig?[]? Children { get; set; }
    public int?  KeyCode      { get; set; }
    public bool  CtrlPressed  { get; set; }
    public bool  ShiftPressed { get; set; }
    public bool  AltPressed   { get; set; }
}
