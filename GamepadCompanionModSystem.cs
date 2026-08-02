using System.Linq;
using GamepadCompanion.Actions;
using GamepadCompanion.Gui;
using GamepadCompanion.Input;
using GamepadCompanion.Toggles;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace GamepadCompanion;

public class GamepadCompanionModSystem : ModSystem
{
    private const string ConfigFile = "gamepadcompanion.json";

    private ICoreClientAPI? capi;
    private IGamepadProvider? gamepad;
    private GamepadInputDriver? driver;
    private GamepadRenderer? renderer;
    private VirtualCursorRenderer? cursorRenderer;
    private GamepadCompanionConfig? config;
    private ToggleHudOverlay? toggleHud;
    private InputTracer? tracer;

    // Expone el driver para que helpers globales (BuiltinActions, etc.)
    // accedan a estado del input sin acoplarse al ModSystem en cada call.
    public GamepadInputDriver? Driver => driver;

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Client;

    public override void StartServerSide(ICoreServerAPI api)
    {
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        capi = api;
        config = api.LoadModConfig<GamepadCompanionConfig>(ConfigFile) ?? new GamepadCompanionConfig();
        api.StoreModConfig(config, ConfigFile);

        gamepad = new GlfwGamepadProvider(api.Logger);
        driver = new GamepadInputDriver(api, config);
        tracer = new InputTracer(api, gamepad, driver.Toggles, driver.Cursor,
                                 driver.Buttons);
        renderer = new GamepadRenderer(gamepad, driver, tracer);
        toggleHud = new ToggleHudOverlay(api, driver.Toggles);

        // Cargar bindings de la rueda desde config. Si el campo está null
        // (primer arranque) o inválido, BuildDefault da el layout por
        // defecto y luego se persiste para que la próxima sesión arranque
        // con la representación serializada (= forma estable del schema).
        driver.Radial.Bindings = SlotBindings.FromConfig(config.RadialSlots, api);
        config.RadialSlots = driver.Radial.Bindings.ToConfig();

        // Bindings de botones: empty default = todos los botones usan su
        // hardcoded fallback en ButtonMapper. Una entry presente reemplaza
        // ese default para el evento edge-press.
        driver.Buttons.Bindings = ButtonBindings.FromConfig(config.ButtonBindings, api);
        config.ButtonBindings = driver.Buttons.Bindings.ToConfig();

        api.StoreModConfig(config, ConfigFile);

        // Renderer.Before corre cada frame antes del render, en una ventana
        // donde los flags que escribimos a EntityControls sobreviven al
        // physics tick siguiente. Tick listener a 16ms producía tartamudeo
        // porque el flag se escribía después del physics.
        api.Event.RegisterRenderer(renderer, EnumRenderStage.Before);

        // Cursor virtual sobre GUIs: stage Done corre DESPUÉS de Ortho
        // (donde se dibujan dialogs/HUDs vanilla), así nuestro cursor queda
        // por encima de todo. AfterFinalComposition corre ANTES de Ortho
        // (dentro de RenderToFrameBuffers) y por eso el cursor quedaba
        // tapado por los dialogs.
        cursorRenderer = new VirtualCursorRenderer(api, driver.Cursor);
        api.Event.RegisterRenderer(cursorRenderer, EnumRenderStage.Done);

        // Hotkey nativo para abrir el dialog de config sin tener que tipear
        // /gpconfig en chat. Default Insert (raramente usada). Rebindable
        // desde Settings > Controls. Bonus: como es una hotkey con Handler,
        // aparece en el dropdown de slots del propio dialog, así el usuario
        // puede asignarla al radial si quiere.
        api.Input.RegisterHotKey("gpcompanionconfig",
                                 Lang.Get("gamepadcompanion:hotkey-open-config"),
                                 GlKeys.Insert, HotkeyType.HelpAndOverlays);
        api.Input.SetHotKeyHandler("gpcompanionconfig", _ =>
        {
            OpenConfigDialog();
            return true;
        });

        RegisterCommands(api);

        api.Logger.Notification("GamepadCompanion: client started, polling for gamepad");
    }

    private void RegisterCommands(ICoreClientAPI api)
    {
        var parsers = api.ChatCommands.Parsers;

        api.ChatCommands.Create("gpdumphotkeys")
            .WithDescription("Lists all hotkey codes (for gamepad mapping discovery)")
            .HandleWith(_ =>
            {
                var keys = string.Join("\n  ", api.Input.HotKeys.Keys);
                api.Logger.Notification(
                    $"GamepadCompanion: {api.Input.HotKeys.Count} hotkeys:\n  {keys}");
                return TextCommandResult.Success(
                    $"{api.Input.HotKeys.Count} hotkeys logged to client log");
            });

        api.ChatCommands.Create("gpaxes")
            .WithDescription("Dump raw gamepad axes (for layout debug)")
            .HandleWith(_ =>
            {
                var axes = gamepad?.GetRawAxesSnapshot() ?? System.Array.Empty<float>();
                if (axes.Length == 0) return TextCommandResult.Error("no axes available");
                var formatted = string.Join(" ", axes.Select((v, i) => $"a{i}={v:+0.00;-0.00;0.00}"));
                api.Logger.Notification($"GamepadCompanion: raw axes: {formatted}");
                return TextCommandResult.Success(formatted);
            });

        api.ChatCommands.Create("gptrace")
            .WithDescription("Log per-frame gamepad state for N seconds (default 15, max 60)")
            .WithArgs(parsers.OptionalFloat("seconds", 15f))
            .HandleWith(args =>
            {
                if (tracer is null) return TextCommandResult.Error("tracer not initialized");
                float requested = (float)args[0];
                float actual = tracer.Start(requested);
                return TextCommandResult.Success(
                    $"gptrace started: {actual:F1}s — output to client-main.log");
            });

        api.ChatCommands.Create("gpyaw")
            .WithDescription("Get/set yaw camera sensitivity")
            .WithArgs(parsers.OptionalFloat("value", float.NaN))
            .HandleWith(args =>
            {
                float v = (float)args[0];
                if (!float.IsNaN(v))
                {
                    config!.YawSensitivity = v;
                    api.StoreModConfig(config, ConfigFile);
                }
                return TextCommandResult.Success($"yaw sensitivity = {config!.YawSensitivity}");
            });

        api.ChatCommands.Create("gppitch")
            .WithDescription("Get/set pitch camera sensitivity")
            .WithArgs(parsers.OptionalFloat("value", float.NaN))
            .HandleWith(args =>
            {
                float v = (float)args[0];
                if (!float.IsNaN(v))
                {
                    config!.PitchSensitivity = v;
                    api.StoreModConfig(config, ConfigFile);
                }
                return TextCommandResult.Success($"pitch sensitivity = {config!.PitchSensitivity}");
            });

        api.ChatCommands.Create("gpinvertpitch")
            .WithDescription("Toggle pitch inversion")
            .HandleWith(_ =>
            {
                config!.InvertPitch = !config.InvertPitch;
                api.StoreModConfig(config, ConfigFile);
                return TextCommandResult.Success($"invert pitch = {config.InvertPitch}");
            });

        api.ChatCommands.Create("gpswaptriggers")
            .WithDescription("Toggle swapping the left/right trigger assignment")
            .HandleWith(_ =>
            {
                config!.SwapTriggers = !config.SwapTriggers;
                api.StoreModConfig(config, ConfigFile);
                return TextCommandResult.Success($"swap triggers = {config.SwapTriggers}");
            });

        api.ChatCommands.Create("gpguis")
            .WithDescription("Dump LoadedGuis state (for cursor click debug)")
            .HandleWith(_ =>
            {
                var lines = new System.Collections.Generic.List<string>();
                foreach (var d in api.Gui.LoadedGuis)
                {
                    if (d is null) continue;
                    lines.Add($"{d.GetType().Name} opened={d.IsOpened()} " +
                              $"dlgType={d.DialogType} " +
                              $"recvMouse={d.ShouldReceiveMouseEvents()} " +
                              $"focus={d.Focused}");
                }
                var text = string.Join("\n  ", lines);
                api.Logger.Notification($"GamepadCompanion guis ({lines.Count}):\n  {text}");
                return TextCommandResult.Success(
                    $"{lines.Count} dialogs logged to client log");
            });

        api.ChatCommands.Create("gpconfig")
            .WithDescription("Open GamepadCompanion configuration dialog")
            .HandleWith(_ =>
            {
                if (driver is null)
                    return TextCommandResult.Error("driver not initialized");
                OpenConfigDialog();
                return TextCommandResult.Success("opened gpconfig");
            });
    }

    // Helper: el callback persiste los slots actuales tras cualquier cambio
    // del usuario en el dialog. Centralizado aquí para mantener un único
    // punto de save y evitar que callsites olviden hacerlo.
    private void OpenConfigDialog()
    {
        if (capi is null || driver is null || config is null) return;
        new ConfigDialog(capi, driver.Radial.Bindings, driver.Buttons.Bindings,
                         config, OnConfigChanged).TryOpen();
    }

    // Persiste todo el config (slots + sensibilidad). El dialog escribe
    // directo a las propiedades del objeto config y luego invoca este
    // callback; un único save consolidado tras cada cambio.
    private void OnConfigChanged()
    {
        if (capi is null || driver is null || config is null) return;
        config.RadialSlots    = driver.Radial.Bindings.ToConfig();
        config.ButtonBindings = driver.Buttons.Bindings.ToConfig();
        capi.StoreModConfig(config, ConfigFile);
    }

    public override void Dispose()
    {
        if (capi is not null && renderer is not null)
            capi.Event.UnregisterRenderer(renderer, EnumRenderStage.Before);
        if (capi is not null && cursorRenderer is not null)
            capi.Event.UnregisterRenderer(cursorRenderer, EnumRenderStage.Done);
        renderer = null;
        cursorRenderer = null;
        toggleHud?.TryClose();
        toggleHud?.Dispose();
        toggleHud = null;
        // Bindings de "mantener tecla": soltar antes de tirar el driver, si no
        // el KeyUp nunca sale y la tecla queda apretada en KeyboardState.
        driver?.Buttons.ReleaseHolds();
        driver?.Radial.TryClose();
        driver?.Radial.Dispose();
        gamepad?.Dispose();
        gamepad = null;
        driver = null;
        config = null;
        capi = null;
        base.Dispose();
    }
}
