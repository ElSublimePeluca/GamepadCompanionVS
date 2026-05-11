using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;

namespace GamepadCompanion.Actions;

// Ejecuta una secuencia de IGameAction en orden cuando se dispara el slot.
// Útil para combos típicos como "abrir personaje + abrir inventario" o
// "manual + buscar tema". Las sub-acciones pueden ser cualquier IGameAction
// excepto otra CompositeAction (no anidamos para mantener simple la UI y
// el JSON; si alguien necesita anidar puede aplanar a mano).
//
// La ejecución es síncrona y secuencial: cada sub-acción corre con el
// estado del juego que dejó la anterior. Si una abre un dialog modal, las
// siguientes corren igual (el engine procesa los effects al cerrarse el
// frame, no al medio de Execute).
public sealed class CompositeAction : IGameAction
{
    public IReadOnlyList<IGameAction> Children { get; }
    public string Label { get; }

    public CompositeAction(IEnumerable<IGameAction> children, string? label = null)
    {
        Children = children
            .Where(c => c is not null and not CompositeAction)
            .ToList();
        Label = label ?? BuildDefaultLabel(Children);
    }

    public void Execute(ICoreClientAPI capi)
    {
        foreach (var child in Children)
            child.Execute(capi);
    }

    private static string BuildDefaultLabel(IReadOnlyList<IGameAction> children) =>
        children.Count == 0
            ? "— (vacío)"
            : string.Join(" + ", children.Select(c => c.Label));
}
