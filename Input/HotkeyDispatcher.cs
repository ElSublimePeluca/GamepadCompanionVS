using Vintagestory.API.Client;

namespace GamepadCompanion.Input;

// Wrapper sobre el sistema de hotkeys de VS. capi.Input.TriggerHotKey existe en
// VintagestoryLib pero no está expuesto en la API pública, así que invocamos el
// Handler de la HotKey directamente con su KeyCombination actual.
public sealed class HotkeyDispatcher
{
    private readonly ICoreClientAPI capi;

    public HotkeyDispatcher(ICoreClientAPI capi)
    {
        this.capi = capi;
    }

    public bool Trigger(string code)
    {
        var hk = capi.Input.GetHotKeyByCode(code);
        if (hk?.Handler is null)
        {
            capi.Logger.Warning($"GamepadCompanion: hotkey '{code}' not found or has no handler");
            return false;
        }
        return hk.Handler.Invoke(hk.CurrentMapping);
    }

    public void TriggerOnEdge(string code, bool wasDown, bool isDown)
    {
        if (isDown && !wasDown) Trigger(code);
    }

    public void TriggerOnEdge(string code, GamepadButton button, GamepadState current, GamepadState previous)
    {
        if (current.WasPressed(button, previous)) Trigger(code);
    }
}
