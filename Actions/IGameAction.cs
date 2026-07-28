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

// Acción con semántica de "mientras el botón esté apretado" en vez de
// edge-press. ButtonMapper la maneja por edges de press y release; los
// consumidores que solo ejecutan una vez (rueda radial) caen en Execute.
// Quien la implemente tiene que tolerar Release sin Press previo — el driver
// suelta de más a propósito (foco perdido, radial, teclado virtual) para que
// nunca quede una tecla latcheada.
public interface IHoldableAction
{
    void Press(ICoreClientAPI capi);
    void ReleaseHold(ICoreClientAPI capi);
}
