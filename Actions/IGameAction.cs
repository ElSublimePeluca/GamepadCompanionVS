using Vintagestory.API.Client;

namespace GamepadCompanion.Actions;

// Acción ejecutable disparable desde un slot de la rueda radial, un botón
// remapeado, o cualquier otro consumidor de input. Tres implementaciones
// previstas (M5, M6, M13): HotKeyAction, CompositeInputAction, BuiltinAction.
public interface IGameAction
{
    string Label { get; }
    void Execute(ICoreClientAPI capi);
}
