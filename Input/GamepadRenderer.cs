using System.Diagnostics;
using Vintagestory.API.Client;

namespace GamepadCompanion.Input;

// Pollea el gamepad cada frame de render en stage Before. Reemplaza al tick
// listener del 16ms que producía tartamudeo.
//
// Medimos dt con Stopwatch en lugar de usar el deltaTime que pasa el engine —
// ese viene en una unidad no documentada y nuestras pruebas con stick R
// daban velocidad de cámara ~1000× lo esperado, sugiriendo que llega en ms o
// está acumulado a una frecuencia mucho más alta que 60Hz. Wall-clock garantiza
// que la sensibilidad final sea independiente de FPS y predecible.
public sealed class GamepadRenderer : IRenderer
{
    private readonly IGamepadProvider gamepad;
    private readonly GamepadInputDriver driver;
    private readonly InputTracer tracer;
    private GamepadState previousState;
    private long lastTimestamp;

    public GamepadRenderer(IGamepadProvider gamepad, GamepadInputDriver driver,
                           InputTracer tracer)
    {
        this.gamepad = gamepad;
        this.driver = driver;
        this.tracer = tracer;
        previousState = GamepadState.Disconnected;
    }

    public double RenderOrder => 0;
    public int RenderRange => 0;

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        long now = Stopwatch.GetTimestamp();
        float dt = lastTimestamp == 0
            ? 1f / 60f
            : (float)((now - lastTimestamp) / (double)Stopwatch.Frequency);
        lastTimestamp = now;

        var state = gamepad.Poll();
        driver.OnTick(state, previousState, dt);
        // Trace después de OnTick para que veamos los toggles/EntityControls
        // *post* aplicación del frame actual — refleja el estado que el
        // engine usará en el physics tick siguiente.
        tracer.Capture(state);
        previousState = state;
    }

    public void Dispose() { }
}
