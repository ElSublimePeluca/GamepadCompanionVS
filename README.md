# GamepadCompanion

Soporte nativo de gamepad para [Vintage Story](https://www.vintagestory.at/) 1.22+. Funciona como mod cliente, sin necesidad de Steam Input, Antimicro ni mappers externos.

> **Estado**: usable, probado a fondo en Linux con un GameSir Cyclone 2. Cross-platform/cross-controller compatible en teoría — ver [compatibilidad](#compatibilidad) abajo.

> **Idioma de UI**: localizado vía `Lang.Get()` con archivos en `assets/gamepadcompanion/lang/` — inglés (`en.json`, fallback), español latino (`es-419.json`) y español peninsular (`es-es.json`). El idioma se toma del cliente de VS. Para contribuir otro idioma alcanza con agregar el JSON correspondiente; PRs bienvenidos.

## Qué hace

- **Movimiento y cámara** con sticks. Sensibilidad horizontal/vertical configurable, dead zone ajustable, opción de invertir pitch.
- **Acciones contextuales**: B cierra el dialog abierto o suelta el item activo según contexto; A salta (siempre); X/Y/Back/Start mapeados a tool mode, inventario, mapa, menú.
- **Rueda radial** de 12 slots configurables (mantener LB o DPad ↑ según la disposición, + stick derecho). Defaults para Personaje, Chat, Manual, Configurar, Teclado virtual; el resto se asigna desde el dialog.
- **Dos disposiciones de botones**, clásica (1.13) y nueva (1.14), intercambiables desde la tab Botones. Al actualizar se pregunta una vez; cambiarlas no pisa lo que hayas asignado a mano.
- **Cursor virtual sobre GUIs**: cuando hay un dialog modal abierto aparece un cursor amarillo. El stick derecho lo mueve libre, como un mouse; el DPad lo hace saltar al slot, receta, pestaña o botón más cercano en esa dirección — pensado para navegar inventarios rápido. RT o A hacen clic izquierdo y LT clic derecho.
- **Toggles de agacharse y correr** con L3/R3, indicador en HUD esquina superior derecha (si el minimapa está pinneado ahí, el indicador se acomoda justo debajo). Son las teclas Shift y Ctrl del juego, así que además de agacharse y correr sirven de modificador para los clicks: shift+click en inventario, ctrl+click para colocar, etc.
- **Modo precisión** con DPad ↑: divide la sensibilidad de cámara por una fracción (default 0.3x) para apuntar bloques específicos.
- **Acciones compuestas**: una sola slot del radial puede ejecutar varias acciones en secuencia.
- **Inyección de teclas individuales** (`KeyPressAction`): bindeás cualquier tecla del teclado a un slot/botón del gamepad. Útil para hotkeys que no aparecen en la lista vanilla.
- **Teclado virtual on-screen** para tipear comandos y chat con el gamepad. QWERTY + `/` `.` para `/comandos`. DPad navega, A escribe, B cierra.
- **Íconos del mando en las ayudas del juego**: el cartel que aparece al mirar un bloque, el hint del ítem en mano, el manual y los tooltips muestran el botón del mando en vez de la tecla o el ícono de mouse. Se elige el estilo (Xbox / PlayStation / Nintendo) o se deja en automático, que lo deduce del mando conectado. Ver [Íconos en las ayudas](#íconos-en-las-ayudas).
- **Editor in-game** (`.gpconfig` o tecla Insert por default): tabs para Rueda, Botones del gamepad, Sensibilidad y Ayudas. Persistencia automática a JSON.

## Requisitos

- Vintage Story **1.22+**
- Un gamepad compatible (ver compatibilidad)
- En Linux: el controller debe estar detectado por el kernel como joystick (`/dev/input/js*`). Esto incluye casi cualquier Xbox controller, GameSir, 8BitDo, etc.

## Compatibilidad

### Plataformas

| Plataforma | Estado | Notas |
|------------|--------|-------|
| Linux      | ✅ Probado a fondo | Probado en CachyOS (X11) |
| Windows    | ⚠️ Untested        | El stack es cross-platform (GLFW/OpenTK/VS API), debería funcionar pero no se verificó |
| macOS      | ⚠️ Untested        | Mismo caso que Windows |

### Controllers

El mod bypassea la "gamepad mapping" de GLFW (la SDL DB) y lee el joystick raw, detectando automáticamente uno de tres layouts en el primer poll:

- **Xpad** (default): XInput / Xbox controllers en Linux y Windows. Axes en orden LX,LY,LT,RX,RY,RT; botones A,B,X,Y,LB,RB,Back,Start,Guide,L3,R3.
- **DS4-DirectInput**: PS4 y compatibles en Windows (DirectInput). Firma: triggers signed en axes 3/4 (reposo = -1). Axes en orden LX,LY,RX,LT,RT,RY; face buttons Square,Cross,Circle,Triangle.
- **GameSir-PS4**: GameSir Cyclone 2 (y probablemente otros del mismo fabricante) en modo PS4. Detectado por nombre del dispositivo ("Chicken Run" o "GameSir"). Axes en orden xpad estándar pero con triggers signed; face buttons X y Y intercambiados en raw 2/3.

En los layouts PS4 el mod ignora los raw buttons 6/7 (L2/R2 como botón digital) para evitar acciones fantasma cuando se aprietan los triggers analógicos.

| Controller | Estado |
|------------|--------|
| GameSir Cyclone 2 (modo Xbox) | ✅ Probado a fondo (Linux) |
| GameSir Cyclone 2 (modo PS4)  | ✅ Probado (Linux) |
| Xbox 360 / One / Series | ✅ Layout xpad — debería funcionar |
| PS4-like genérico DirectInput (Wired Controller, etc.) | ✅ Detectado por firma de triggers signed |
| DualShock 4 / DualSense oficiales | ⚠️ Depende del driver — algunos firmwares caen en una de las heurísticas, otros no |
| Switch Pro Controller | ⚠️ Depende del modo |
| Joysticks vintage / no-xpad / no-PS4 | ❌ Layout desconocido, mapeos van a salir mal |

Si tu controller no funciona, podés diagnosticar con `.gpaxes` (dump raw de los axes). El log del cliente también imprime el layout detectado al conectar (`detected ... layout`) y la lista completa de joysticks presentes (`candidates at connect`).

### El personaje camina o gira solo, o el mod ignora tu control

El mod se queda con el primer joystick que el sistema le muestra, y no todo lo que el sistema
llama "joystick" es un gamepad: controladoras RGB de placa madre, teclados con teclas
programables y otros HID aparecen con la misma forma (botones + ejes). Si el mod agarra uno de
esos, el resultado típico es un personaje que camina en diagonal y una cámara que gira sola,
mientras tu control real ni figura.

Corré `.gpdevice` para ver todos los joysticks presentes: el listado dice cuál está en uso y por
qué descartó a los demás. Si el elegido no es el tuyo, `.gpdevice <número>` fuerza el correcto y
lo guarda en el config para las próximas sesiones (`.gpdevice auto` deshace la elección).

### Steam Input

Si lanzás Vintage Story desde Steam, **desactivá Steam Input para este juego** o vas a tener doble input (Steam manda kb/m sintéticos + el mod maneja el gamepad en paralelo → cámara al doble de velocidad, clicks duplicados, etc.).

Cómo hacerlo, sin afectar tus otros juegos:

1. Steam → librería → clic derecho en **Vintage Story** → **Properties** → **Controller**
2. "Override for Vintage Story" → **Disable Steam Input**
3. Cerrar y relanzar VS

El resto de las features de Steam (overlay, friends, tiempo de juego, screenshots) siguen funcionando — solo desactivás la capa de remapping de gamepad. Este mod es básicamente una alternativa a Steam Input *específica para VS*, con conocimiento de las hotkeys y dialogs del juego que Steam Input no puede tener. Usá uno o el otro, no los dos.

Si tu controller solo aparece como gamepad cuando Steam Input lo emula (típico de DualSense / DualShock 4 / Switch Pro), tenés dos caminos:

- **Steam Input en modo "Gamepad" / "X360 passthrough"** (no kb/m remap): Steam expone el controller como Xbox 360 virtual y el mod lo lee normal vía GLFW. Sin doble input.
- **Driver alternativo** fuera de Steam (DS4Windows en Win, `dualsensectl` / `hid-playstation` en Linux para que el kernel lo exponga como xpad) y desactivar Steam Input por completo.

## Instalación

1. Descargá el zip de [Releases](https://github.com/ElSublimePeluca/GamepadCompanionVS/releases) o cloná y compilá (ver abajo)
2. Copiá la carpeta a `~/.config/VintagestoryData/Mods/` (Linux) o `%appdata%/VintagestoryData/Mods/` (Windows)
3. Arrancá el juego con el gamepad conectado

## Configuración

### Layout default de botones

Hay dos **disposiciones** de botones, y la fila "Disposición" de la tab Botones cambia entre
ellas en un click. Lo que el usuario haya asignado a mano queda por encima de las dos, así que
cambiar de disposición nunca borra configuración.

| Botón     | Nueva (1.14) | Clásica (1.13) |
|-----------|--------------|----------------|
| A         | Saltar (hold) | igual |
| B         | Cerrar dialog si hay uno abierto, sino soltar item activo | igual |
| X         | Tool mode | igual |
| Y         | Inventario | igual |
| LB        | Hotbar slot anterior (mantener = recorre) | **abrir la rueda** (hold) |
| RB        | Hotbar slot siguiente (mantener = recorre) | sin asignar |
| RT        | Click izquierdo / atacar / minar | igual |
| LT        | Click derecho / interactuar / colocar | igual |
| Back      | Mapa | igual |
| Start     | Menú de pausa | igual |
| L3        | Toggle correr (Ctrl) | igual |
| R3        | Toggle agacharse (Shift) | igual |
| DPad ↑    | **abrir la rueda** (hold) | Toggle modo precisión |
| DPad ↓    | Sentarse (la tecla de `sitdown`, G por default) | igual |
| DPad ←    | Toggle modo precisión | Hotbar slot anterior |
| DPad →    | Personaje | Hotbar slot siguiente |

Al actualizar desde 1.13 el mod **no** cambia nada solo: arranca con la clásica y pregunta una
vez, al entrar al mundo, cuál preferís. Una instalación nueva arranca con la nueva y no pregunta.
El botón que abre la rueda queda reservado: no ejecuta binding ni default, y la tab Botones
muestra esa fila como "Rueda radial (mantener)" para que se vea dónde quedó.

Con un dialog abierto aparece el cursor virtual y cambian dos cosas: el **stick derecho** mueve el
cursor libre, como un mouse, y el **DPad** deja de hacer lo de la tabla y salta al slot, receta,
pestaña o botón más cercano en esa dirección. RT o A hacen clic izquierdo donde está el cursor (son
el mismo botón, así que apretar los dos no da dos clics) y LT hace clic derecho. Si le asignaste
algo a A desde `.gpconfig`, A hace eso y el clic queda en RT. En el mapa a pantalla completa, DPad
↑/↓ hacen zoom. En los menús que no tienen slots ni botones del juego, como los hechos con ImGui (el
de xSkills), el DPad mueve el cursor de a un paso y los clics también les llegan.

Todo configurable desde `.gpconfig`.

### Mantener una tecla (mods con modificador)

Varios mods usan una tecla como **modificador durante un click** (RKN Crafting: mantener Alt +
click derecho). Un binding de tecla normal no sirve: manda down y up en el mismo frame, así que
la tecla ya está soltada cuando llega el click.

Para eso está la entrada **`[Tecla: mantener mientras el botón esté apretado]`** en el picker
(tab Botones → botón → elegir esa entrada → apretar la tecla). La tecla queda apretada mientras
el botón del gamepad lo esté, por el pipeline real de teclado del engine, así que un mod la ve
igual que si viniera de un teclado físico. Un modificador pelado (Alt/Ctrl/Shift) se captura
siempre como "mantener", aunque se entre por la entrada de tecla individual.

**Receta para RKN Crafting:** bindear `AltLeft` como tecla a mantener en el botón que quieras
(A, por ejemplo), después mantener ese botón y usar LT. No hace falta remapear nada en RKN.

Ojo con un efecto de vanilla, no del mod: `AltLeft` es también la tecla **Lock/Unlock Mouse
Cursor** del juego, así que mientras la mantengas el mouse queda libre — la cámara del stick
derecho no gira y el HUD de slots de RKN aparece, que es exactamente lo que ese flujo necesita.
La puntería sigue siendo el centro de la pantalla mientras no muevas el mouse físico.

### Íconos en las ayudas

Con un mando conectado, las ayudas que dibuja el propio juego muestran el botón del mando en vez
de la tecla: donde decía "🖱 derecho: Abrir" pasa a decir **`[LT]: Abrir`**, y donde decía
"Shift + 🖱 derecho: Poner en la pila" pasa a decir **`[RS*] + [LT]`**.

Alcanza al cartel del bloque mirado, al hint del ítem que tenés en mano, al manual y a los
tooltips. Quedan sin tocar a propósito **Ajustes > Controles** (ahí las teclas tienen que seguir
siendo teclas) y los números del hotbar (con mando el D-pad *cicla* slots, así que no hay botón
que mostrar).

En el cartel la conversión es **todo o nada por línea**: si alguna parte de la línea no se puede
traducir con honestidad, la línea entera se queda en teclado. Media línea en glifos y media en
teclado se lee peor que ninguna.

El estilo se elige en `.gpconfig` → tab **Ayudas**, o con `.gpglyphs`:

| Estilo | Cara de abajo | Gatillos | Stick apretado |
|---|---|---|---|
| Xbox | `A` | `LT` `RT` | `LS` `RS` |
| PlayStation | ✕ (símbolo) | `L2` `R2` | `L3` `R3` |
| Nintendo | `B` | `ZL` `ZR` | `L3` `R3` |

En PlayStation las cuatro caras se dibujan con **los símbolos de verdad** (✕ ○ □ △), no
con las palabras. El resto son etiquetas de texto porque es lo que dice el plástico.
Los símbolos son formas vectoriales, no imágenes: se dibujan con el mismo contorno que
las letras de al lado, así que se leen igual sobre el mundo que sobre el pergamino del
manual, y a cualquier escala de interfaz.

En **automático** la familia sale del nombre del mando, y la tab muestra entre paréntesis qué
resolvió. Hay casos que por software son indistinguibles — un GameSir Cyclone 2 en modo PS4 se
declara con el vendor id de Sony aunque tenga serigrafía A/B/X/Y — y para eso está el override
manual.

**La marca `*`**: una etiqueta como `RS*` quiere decir que en este mod ese botón es un
**toggle**, no una tecla que se mantiene. L3 y R3 activan y desactivan Ctrl y Shift de una
pulsación, así que `[RS*] + [LT]: Poner en la pila` se hace apretando RS, después LT, y
después RS otra vez para desactivarlo — no manteniendo RS. Sin la marca, el cartel estaría
diciendo algo que no es.

Si el mando está conectado pero preferís las ayudas de teclado, `.gpglyphs off`.

### Comandos chat

Todos son comandos de **cliente**, así que van con **punto**, no con barra: `.gpconfig`, no
`/gpconfig`. En Vintage Story `.` habla con el cliente y `/` con el servidor — un `/gpconfig`
se le manda al servidor, que no lo conoce, y contesta "ese comando no existe".

| Comando        | Qué hace |
|----------------|----------|
| `.gpconfig`    | Abre el dialog de configuración (también con tecla Insert) |
| `.gpdumphotkeys` | Lista todas las hotkeys registradas en el log |
| `.gpaxes`      | Dump raw de los axes del gamepad (debug) |
| `.gpdevice`    | Lista los joysticks que ve el juego; `.gpdevice <n>` fuerza uno, `.gpdevice auto` vuelve a autodetección |
| `.gpglyphs`    | Estado completo de los íconos del mando en las ayudas: mando detectado, familia resuelta y por qué, y qué botón muestra cada tecla. `.gpglyphs <off\|auto\|xbox\|playstation\|nintendo>` lo fija |
| `.gpyaw <val>` | Set sensibilidad horizontal de cámara |
| `.gppitch <val>` | Set sensibilidad vertical |
| `.gpinvertpitch` | Toggle invertir pitch |
| `.gpguis`      | Dump dialogs abiertos (debug) |

### Archivo de config

`~/.config/VintagestoryData/ModConfig/gamepadcompanion.json` — generado en el primer arranque. Borrarlo regenera defaults. Desde el dialog hay un botón "Restaurar predeterminados" en la tab Rueda.

## Build desde código

Requiere .NET 10 SDK. Si tenés el juego en la ubicación por defecto (`~/.local/share/vintagestory` en Linux, `%appdata%/Vintagestory` en Windows) alcanza con:

```bash
cd GamepadCompanion
dotnet build
```

Si lo tenés en otro lado, apuntá la variable de entorno `VINTAGE_STORY` al directorio de instalación (el que contiene `VintagestoryAPI.dll`):

```bash
export VINTAGE_STORY=/ruta/a/vintagestory
```

El output queda en `bin/Debug/Mods/mod/` — copiá esa carpeta (o usá `--addModPath` al iniciar VS) para probar.

## Cómo funciona internamente (disclaimer)

Para que las GUIs vanilla del juego (inventario, knapping, anvil, dialogs varios) reaccionen al gamepad sin tener que parchearlas una por una, el mod **inyecta eventos sintéticos de mouse y teclado** directamente en el motor de Vintage Story:

- El cursor virtual escribe `ClientMain.MouseCurrentX/Y` y sincroniza la posición del cursor del SO vía `GLFW.SetCursorPos`.
- Los clicks con RT/LT invocan `OnMouseDown` / `OnMouseUp` con `EnumMouseButton.Left` / `Right`.
- El teclado virtual y `KeyPressAction` invocan `OnKeyDown`, `OnKeyPress` (para chars imprimibles) y `OnKeyUp` en `ClientMain`.
- Los toggles de Ctrl/Shift escriben en `ClientMain.KeyboardState[]` para que el resto del juego vea las modifier keys como presionadas.

Todo esto es legítimo dentro del modelo de mods de VS (la API está pública), pero implica que el mod tiene acceso al stack de input del cliente y puede generar eventos que el juego trata como si vinieran del usuario. **Si te incomoda ese patrón para tu setup, no uses el mod.** No envía nada a la red, no toca archivos fuera de `ModConfig/gamepadcompanion.json`, y no tiene bloques `unsafe`, pero es honesto decir cómo trabaja antes de que lo instales.

## Licencia y créditos

MIT — ver [LICENSE](LICENSE).

Los cuatro símbolos de las caras de PlayStation los dibuja el mod como formas
vectoriales: no se empaqueta arte de terceros. El pack [Kenney Input
Prompts](https://kenney.nl/assets/input-prompts) (CC0) se usó como referencia visual
mientras se decidía cómo tenían que verse.
