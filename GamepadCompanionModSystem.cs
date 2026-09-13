using System.Linq;
using GamepadCompanion.Actions;
using GamepadCompanion.Glyphs;
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
    private GlyphSession? glyphs;

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
        // Los estáticos de ScreenManager viven lo que vive el proceso, pero el
        // ModSystem se reinstancia en cada carga de mundo: rearmamos el espejo
        // para que un ClearAll() de la sesión anterior no lo deje apagado.
        ScreenInputMirror.Reset();
        config = LoadConfigSafe(api);
        api.StoreModConfig(config, ConfigFile);

        gamepad = new GlfwGamepadProvider(api.Logger)
        {
            PreferredDeviceName = config.PreferredDevice,
        };
        driver = new GamepadInputDriver(api, config);
        tracer = new InputTracer(api, gamepad, driver.Toggles, driver.Cursor,
                                 driver.Buttons);
        // Los glifos van adentro de su propio try/catch que NUNCA relanza: si algo
        // acá tira, StartClientSide se corta y el ModLoader saca al mod entero de
        // enabledSystems (ni siquiera corre Dispose). Los hints valen bastante
        // menos que el gamepad, así que en el peor caso arrancamos sin ellos.
        try
        {
            glyphs = new GlyphSession(api, config, gamepad,
                                      () => driver.Buttons.Bindings,
                                      () => driver.Radial.Bindings,
                                      Mod.Info.Version);
            GlyphRuntime.Session = glyphs;
            // El painter se registra SIEMPRE, con o sin mando: la decisión de
            // dibujar glifo o ícono de vanilla se toma adentro del delegate
            // leyendo estado vivo, así que enchufar el mando no re-registra
            // nada, sólo dispara una recomposición.
            new MouseIconOverride(api).Install();
            glyphs.Icons.MarkActive();
            var invalidator = new GlyphInvalidator(api, glyphs);
            invalidator.Wire();
            glyphs.Invalidator = invalidator;
            // Los parches van al final: si algo de esto falla, lo que se pierde
            // son las cápsulas de tecla, no los íconos de mouse ni el resto.
            // ApplyOnce nunca relanza y nunca despatchea.
            GlyphPatcher.ApplyOnce(api, glyphs, Mod.Info.ModID);
        }
        catch (System.Exception e)
        {
            // Los dos, o el painter seguiría dibujando contra una sesión que el
            // resto del mod ya da por muerta.
            glyphs = null;
            GlyphRuntime.Clear();
            api.Logger.Warning(
                "GamepadCompanion: gamepad glyph hints could not start; " +
                "the rest of the mod works normally.");
            api.Logger.Warning(e);
        }

        renderer = new GamepadRenderer(gamepad, driver, tracer, InvalidateGlyphs);
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
        // .gpconfig en chat (comando de CLIENTE: punto, no barra). Default
        // Insert (raramente usada). Rebindable
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

        // Salida del mundo (menú Escape, muerte, kick, desconexión, crash del
        // hilo de cliente: todos pasan por ClientMain.DestroyGameSession, que
        // dispara este evento como primera cosa). Es la única ventana donde
        // todavía podemos mandar el MouseUp/KeyUp de verdad; en Dispose el
        // ClientMain ya está marcado como disposed y los ignora en silencio.
        api.Event.LeaveWorld += OnLeaveWorld;

        RegisterCommands(api);
        // Le preguntamos al engine cuáles quedaron REGISTRADOS de verdad, en vez
        // de asumir que Create() alcanzó. Un comando que no aparece acá no
        // existe para el chat, y sin este renglón la diferencia entre "no se
        // registró" y "lo tipeaste distinto" no se puede ver desde el log.
        LogRegisteredCommands(api);

        api.Logger.Notification("GamepadCompanion: client started, polling for gamepad");
    }

    // LoadModConfig es un JsonConvert.DeserializeObject pelado sobre el archivo
    // (Vintagestory.Common.APIBase.LoadModConfig): una coma de más, un campo
    // con el tipo cambiado o un enum con un valor que todavía no conocemos y
    // tira DENTRO de StartClientSide. Eso no degrada nada, se lleva el mod
    // ENTERO: el ModLoader saca al sistema de enabledSystems y deja un solo
    // renglón en el log; y como Dispose recorre esa misma lista, tampoco corre
    // la limpieza. El usuario ve el gamepad muerto sin ninguna pista.
    //
    // Acá lo bajamos a "arranca con los defaults". El backup del archivo va
    // ANTES de devolver, porque el StoreModConfig que viene justo después lo
    // pisa con los defaults y ese archivo es la única copia de la config del
    // usuario. El nombre lleva fecha y hora para no tapar un backup anterior.
    private static GamepadCompanionConfig LoadConfigSafe(ICoreClientAPI api)
    {
        try
        {
            return api.LoadModConfig<GamepadCompanionConfig>(ConfigFile)
                   ?? new GamepadCompanionConfig();
        }
        catch (System.Exception e)
        {
            string path = System.IO.Path.Combine(GamePaths.ModConfig, ConfigFile);
            string? backup = BackupBrokenConfig(api, path);
            api.Logger.Error(
                "GamepadCompanion: could not read {0}, starting with default " +
                "settings. {1}", path,
                backup is null
                    ? "The unreadable file could NOT be backed up and is about " +
                      "to be overwritten."
                    : $"The unreadable file was kept as {backup}.");
            api.Logger.Error(e);
            return new GamepadCompanionConfig();
        }
    }

    private static string? BackupBrokenConfig(ICoreClientAPI api, string path)
    {
        try
        {
            if (!System.IO.File.Exists(path)) return null;
            // Copy y no Move: si el StoreModConfig de después falla, que el
            // usuario siga teniendo su archivo donde lo dejó.
            string backup = $"{path}.{System.DateTime.Now:yyyyMMdd-HHmmss}.bak";
            System.IO.File.Copy(path, backup, overwrite: true);
            return backup;
        }
        catch (System.Exception e)
        {
            api.Logger.Warning(
                "GamepadCompanion: could not back up the unreadable config.");
            api.Logger.Warning(e);
            return null;
        }
    }

    private static void LogRegisteredCommands(ICoreClientAPI api)
    {
        try
        {
            var found = new System.Collections.Generic.List<string>();
            foreach (var entry in api.ChatCommands)
                if (entry.Key.StartsWith("gp", System.StringComparison.Ordinal))
                    found.Add(entry.Key);
            found.Sort(System.StringComparer.Ordinal);
            api.Logger.Notification(
                "GamepadCompanion: {0} chat commands registered (type them with a DOT, " +
                "they are client commands): .{1}",
                found.Count, string.Join("  .", found));
        }
        catch (System.Exception e)
        {
            api.Logger.Warning("GamepadCompanion: could not list the registered chat commands.");
            api.Logger.Warning(e);
        }
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

        // Escape hatch para cuando la autodetección elige el joystick
        // equivocado: no hay heurística que cubra todos los HID con forma de
        // gamepad que existen, pero el usuario sí ve cuál es el suyo.
        api.ChatCommands.Create("gpdevice")
            .WithDescription("List joysticks, or force one: .gpdevice <jid> | auto")
            .WithArgs(parsers.OptionalWord("jid"))
            .HandleWith(args =>
            {
                if (gamepad is null) return TextCommandResult.Error("gamepad provider not initialized");
                string? arg = args[0] as string;

                if (string.IsNullOrWhiteSpace(arg))
                    return ListDevices(api);

                if (arg.Equals("auto", System.StringComparison.OrdinalIgnoreCase) ||
                    arg.Equals("clear", System.StringComparison.OrdinalIgnoreCase))
                {
                    gamepad.ResetSelection();
                    config!.PreferredDevice = null;
                    api.StoreModConfig(config, ConfigFile);
                    InvalidateGlyphs();
                    return TextCommandResult.Success("gamepad device = auto");
                }

                if (!int.TryParse(arg, out int jid))
                    return TextCommandResult.Error($"expected a joystick id or 'auto', got '{arg}'");
                if (!gamepad.SelectDevice(jid))
                    return TextCommandResult.Error($"no joystick present on jid={jid}");

                config!.PreferredDevice = gamepad.PreferredDeviceName;
                api.StoreModConfig(config, ConfigFile);
                InvalidateGlyphs();
                return TextCommandResult.Success(
                    $"gamepad device = jid {jid}: {gamepad.DeviceName} (saved)");
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
                // El mapa inverso tiene el gatillo de cada click cacheado.
                InvalidateGlyphs();
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

        // Sin argumento: volcado completo del estado de los glifos, al chat y al
        // log. Con argumento: fija la familia, la persiste y rearma el mapa.
        // Misma forma que .gpdevice, que ya usa OptionalWord + StoreModConfig.
        api.ChatCommands.Create("gpglyphs")
            .WithDescription("Show gamepad glyph hint state, or set it: " +
                             ".gpglyphs <off|auto|xbox|playstation|nintendo>")
            .WithArgs(parsers.OptionalWord("style"))
            .HandleWith(args =>
            {
                if (glyphs is null)
                    return TextCommandResult.Error("glyph hints failed to start, see the client log");

                string? arg = args[0] as string;
                if (string.IsNullOrWhiteSpace(arg))
                {
                    // El log PRIMERO: si armar el volcado o el resumen tirara, al
                    // menos queda constancia de que el comando corrió. Con el
                    // orden inverso, un fallo acá se ve exactamente igual que un
                    // comando que no existe.
                    api.Logger.Notification("GamepadCompanion: .gpglyphs");
                    string dump = GlyphDiagnostics.Describe(glyphs);
                    api.Logger.Notification("GamepadCompanion:\n" + dump);
                    return TextCommandResult.Success(GlyphDiagnostics.Summarize(glyphs));
                }

                // Parse tolerante, pero acá SÍ rechazamos lo que no entendemos:
                // en el config una porquería tiene que degradar a "auto", pero un
                // comando tipeado a mano tiene que decir que no se entendió, o el
                // usuario cree que fijó playstation y le quedó auto.
                var parsed = GlyphStyles.Parse(arg);
                if (parsed == GlyphStyle.Auto &&
                    !arg.Equals("auto", System.StringComparison.OrdinalIgnoreCase))
                    return TextCommandResult.Error(
                        $"unknown glyph style '{arg}' — use off, auto, xbox, playstation or nintendo");

                config!.GlyphStyle = GlyphStyles.Serialize(parsed);
                api.StoreModConfig(config, ConfigFile);
                InvalidateGlyphs();
                return TextCommandResult.Success(
                    $"glyph style = {config.GlyphStyle} (showing: {glyphs.Resolver.Style})");
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

    // El listado va al chat (para que el usuario elija sin salir del juego) y
    // al log (para que entre en un reporte de bug sin pedirle nada más).
    private TextCommandResult ListDevices(ICoreClientAPI api)
    {
        var devices = gamepad!.ScanDevices();
        if (devices.Count == 0)
        {
            api.Logger.Notification("GamepadCompanion: gpdevice found no joysticks present");
            return TextCommandResult.Success(
                "no joysticks present (on Flatpak check that /dev/input is shared)");
        }

        var lines = new System.Collections.Generic.List<string>();
        foreach (var d in devices)
        {
            string mark = d.Selected ? " <- in use" : "";
            string state = d.Eligible ? "eligible" : $"skipped: {d.RejectReason}";
            lines.Add($"[{d.Jid}] {d.Name} (buttons={d.Buttons}, axes={d.Axes}, " +
                      $"hats={d.Hats}) {state}{mark}");
        }
        string pref = gamepad.PreferredDeviceName is { } p ? $"\"{p}\"" : "auto";
        string text = string.Join("\n", lines);
        api.Logger.Notification(
            $"GamepadCompanion: gpdevice preference={pref}\n  " +
            string.Join("\n  ", lines));
        return TextCommandResult.Success(
            $"preference: {pref}\n{text}\nUse .gpdevice <number> to force one, " +
            $".gpdevice auto to undo.");
    }

    // Un solo punto de invalidación: cualquier cosa que cambie qué botón hace
    // qué tecla pasa por acá. Null-safe a propósito — si los glifos no
    // arrancaron, el resto del mod sigue llamando a esto sin enterarse.
    private void InvalidateGlyphs() => glyphs?.Resolver.Invalidate();

    private void OnLeaveWorld()
    {
        // ClientEventAPI.Trigger aísla las excepciones de los handlers, así
        // que si algo acá tira no se lleva puesto el teardown del juego.
        //
        // La sesión de glifos se suelta ACÁ y no sólo en Dispose: LeaveWorld sale
        // de ClientMain.DestroyGameSession, así que cubre todos los caminos de
        // salida, mientras que Dispose puede no correr nunca. Un delegate de
        // CustomIcons — o, más adelante, un parche de Harmony — sigue instalado
        // después del teardown, y con una sesión que apunta a un mundo muerto
        // reventaría contra una API destruida.
        GlyphRuntime.Clear();
        driver?.ReleaseAll();
        ScreenInputMirror.ClearAll();
        NativeMouseMirror.ClearAll();
    }

    // Helper: el callback persiste los slots actuales tras cualquier cambio
    // del usuario en el dialog. Centralizado aquí para mantener un único
    // punto de save y evitar que callsites olviden hacerlo.
    private void OpenConfigDialog()
    {
        if (capi is null || driver is null || config is null) return;
        new ConfigDialog(capi, driver.Radial.Bindings, driver.Buttons.Bindings,
                         config, glyphs, OnConfigChanged).TryOpen();
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
        // Rebindear un botón cambia qué tecla muestra su glifo.
        InvalidateGlyphs();
    }

    public override void Dispose()
    {
        // Primero y aislado: soltar la sesión de glifos no puede quedar detrás
        // de nada que pueda tirar, porque lo que sigue incluye el
        // ScreenInputMirror.ClearAll() que destraba las teclas.
        try { GlyphRuntime.Clear(); } catch { }
        glyphs = null;

        if (capi is not null)
            capi.Event.LeaveWorld -= OnLeaveWorld;
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
        // el KeyUp nunca sale y la tecla queda apretada en KeyboardState. Ojo:
        // acá ClientMain ya tiene disposed=true y OnKeyUp retorna sin hacer
        // nada — el release que sirve es el de OnLeaveWorld. Esto queda como
        // limpieza de nuestro propio heldByButton.
        driver?.Buttons.ReleaseHolds();
        // Backstop del espejo: escritura directa a los estáticos, sin pasar
        // por el engine (UpdateMouseButtonState no chequea `disposed` y
        // caminaría ClientSystems ya dispuestos). Después de esto el espejo
        // queda apagado, así que el `finally` del driver — que sigue
        // corriendo mientras se desarma el stack de un Dispose reentrante
        // desde el menú Escape — ya no puede volver a escribir nada.
        ScreenInputMirror.ClearAll();
        // Lo mismo con los botones de OpenTK que lee ImGui.
        NativeMouseMirror.ClearAll();
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
