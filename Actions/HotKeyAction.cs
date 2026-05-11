using Vintagestory.API.Client;

namespace GamepadCompanion.Actions;

// Dispara una hotkey vanilla del juego (o de cualquier mod) por su código.
//
// Misma lógica que HotkeyDispatcher.Trigger, replicada en lugar de pasar el
// dispatcher como dependencia: una acción debe ser autocontenida para poder
// crear instancias desde una config JSON sin saber de los servicios del mod.
//
// `capi.Input.TriggerHotKey()` existe en VintagestoryLib pero no está en la
// API pública, así que invocamos `Handler.Invoke(CurrentMapping)` directamente.
// Si el handler es null (caso típico de hotkeys de movimiento que el engine
// procesa via flags) o la hotkey no existe, log warning y no-op.
public sealed class HotKeyAction : IGameAction
{
    public string Code { get; }
    public string Label { get; }

    public HotKeyAction(string hotkeyCode, string? label = null)
    {
        Code = hotkeyCode;
        Label = label ?? hotkeyCode;
    }

    public void Execute(ICoreClientAPI capi)
    {
        var hk = capi.Input.GetHotKeyByCode(Code);
        if (hk?.Handler is null)
        {
            capi.Logger.Warning(
                $"GamepadCompanion: hotkey '{Code}' not found or has no handler");
            return;
        }
        hk.Handler.Invoke(hk.CurrentMapping);
    }
}
