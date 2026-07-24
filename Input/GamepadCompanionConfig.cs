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

    // Slots de la rueda radial. null en el array entero significa "usar
    // defaults"; null en una posición significa "slot vacío". Length 8
    // cuando está poblado. La forma del SlotConfig es plana con
    // discriminador para evitar custom JsonConverter.
    public SlotConfig?[]? RadialSlots { get; set; } = null;

    // Override de la acción edge-press de un botón. Key = nombre del
    // GamepadButton (A, B, DPadLeft, etc.). Una entry presente reemplaza
    // el default hardcoded de ButtonMapper para ese botón. null/ausente
    // = usar el default. LB y RB no son configurables (radial / cursor).
    public Dictionary<string, SlotConfig?>? ButtonBindings { get; set; } = null;
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
