using System.Linq;
using System.Reflection;
using Vintagestory.API.Client;

namespace GamepadCompanion.Actions;

// Abre un GuiDialog encontrándolo por nombre de tipo en `capi.Gui.LoadedGuis`.
// Bypass del sistema de hotkeys: útil para diálogos cuyo `Handler` registrado
// no funciona al invocarse sincrónicamente fuera del pipeline de teclado, ej.
// `HudDialogChat`. Su Handler es un lambda toggle que abre el diálogo, pero
// la lógica de focus del input field no se completa, así que el chat queda
// invisible. `TryOpen()` directo en la instancia abre + focus correctamente.
public sealed class OpenLoadedGuiAction : IGameAction
{
    public string DialogTypeName { get; }
    public string Label { get; }

    public OpenLoadedGuiAction(string dialogTypeName, string label)
    {
        DialogTypeName = dialogTypeName;
        Label = label;
    }

    public void Execute(ICoreClientAPI capi)
    {
        var dialog = capi.Gui.LoadedGuis.FirstOrDefault(
            g => g?.GetType().Name == DialogTypeName);
        if (dialog is null)
        {
            capi.Logger.Warning(
                $"GamepadCompanion: dialog type '{DialogTypeName}' not found in LoadedGuis");
            return;
        }
        if (dialog.IsOpened()) return;

        // OnKeyCombinationToggle es el path que el engine ejecuta cuando el
        // usuario presiona la tecla del toggle. Para dialogs como HudDialogChat,
        // esto incluye setup específico (focus al input field, registro como
        // dialog activo para teclado) que TryOpen() solo no completa.
        // Reusamos la KeyCombination del propio toggle del dialog para que
        // el handler tenga contexto correcto.
        string? toggleCode = dialog.ToggleKeyCombinationCode;
        var combo = toggleCode is not null
            ? capi.Input.GetHotKeyByCode(toggleCode)?.CurrentMapping
            : null;

        if (combo is not null)
        {
            // OnKeyCombinationToggle es protected en GuiDialog, lo invocamos
            // vía reflection. Trade-off aceptado: ata el mod a la firma actual,
            // pero esa firma es estable en VS desde 1.18+.
            var method = dialog.GetType().GetMethod(
                "OnKeyCombinationToggle",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            method?.Invoke(dialog, new object[] { combo });
        }
        else
        {
            dialog.TryOpen();
        }
    }
}
