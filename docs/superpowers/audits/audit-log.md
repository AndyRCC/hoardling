# Registro de auditorías de calidad

> Nota (2026-11-20): el proyecto se llama ahora **Hoardling**. Las entradas anteriores conservan el nombre
> original (DragonPet), sus rutas (`src/DragonPet…`) y su URL de hooks (`/dragonpet`) tal como eran
> al escribirlas.

Una entrada por tarea del plan `docs/superpowers/plans/2026-11-18-dragon-pet.md`.
Cada entrada: qué se revisó, qué se encontró y qué se arregló.

## Tarea 1: entorno y esqueleto
- Revisado: SDK 10.0.404 instalado; la solución compila con 0 warnings (warnings = errores, nullable activo); el test de humo pasa.
- Encontrado: `dotnet add package` eligió System.Management 11.0.0, que pertenece a .NET 11 y no al runtime 10 instalado. Arreglado: fijado a 10.0.0.
- Encontrado: la plantilla de tests traía coverlet, que no se usa. Quitado (menos descargas y menos tiempo de compilación).
- Pendiente consciente: `MainWindow` de plantilla y `StartupUri` se reemplazan en las tareas 12–13.

## Tarea 2: modelos de límites, formato de tiempo y parser de Claude
- RED: 22 tests fallaron con `NotImplementedException`. GREEN: 22/22.
- Encontrado: al editar un test con PowerShell 5.1 (`Get-Content`/`Set-Content`) se corrompieron las tildes ("SesiÃ³n"). Arreglado y regla nueva: los archivos de código solo se editan con herramientas que respetan UTF-8, nunca con PowerShell 5.1.
- Revisado: JSON inválido, que no es un objeto o vacío → `FormatException` (cubierto por tests); `utilization` fuera de 0–100 → se recorta; campos opcionales nulos → se omiten.
- Riesgo abierto: la escala de `utilization` (0–100 o 0–1) solo la conocemos por fuentes de la comunidad. Se verifica con datos reales en la tarea 14.
- Diseño: helpers JSON compartidos en `JsonRead` (interno) para no repetir la lectura tolerante en cada parser.

## Tarea 3: parsers de Antigravity
- RED: 17 tests fallaron con `NotImplementedException`. GREEN: 40/40 en toda la suite.
- Encontrado: Antigravity manda las fechas con 9 decimales y .NET solo acepta 7, así que `lastModifiedTime` se habría leído como nulo sin avisar. Arreglado en `JsonRead.Time`, que recorta a 7 decimales; el test `Trajectories_AllIdle` lo cubre.
- Encontrado en la auditoría: si el CSRF llegaba entre comillas, el token incluía las comillas. Arreglado, con un test nuevo que primero falló y luego pasó.
- Decisión: los estados desconocidos de Antigravity cuentan como inactivo (solo `RUNNING` es trabajando y `WAIT` es te necesita). Así un estado como `ERROR` o `DONE` no deja al dragón trabajando para siempre. Se verifica en vivo en la tarea 14.
- Revisado: bucket sin `remainingFraction` = 100 % usado (Review Focus 1, con test).

## Tarea 4: trackers de actividad
- RED: 27 tests fallaron. GREEN: 67/67 en toda la suite; build con 0 warnings.
- Revisado, hilos: `ClaudeActivityTracker` recibe eventos desde el hilo del servidor de hooks y se lee desde el coordinador, así que usa un `Lock`. `AntigravityActivityTracker` solo se usa desde el coordinador.
- Revisado, memoria: las sesiones viejas (más de 15 min) se borran al consultarlas, así que el diccionario no crece sin límite si Claude Code muere sin `SessionEnd`.
- Revisado, falsos positivos: el primer snapshot después de arrancar, o después de que Antigravity se cierre, no activa el respaldo por `lastModifiedTime` (tests `FirstSnapshot_*` y `AfterUnavailable_*`).
- Mejora: el orden de prioridad de `AppActivity` era implícito; ahora está documentado en el enum.
- Riesgo abierto: abrir una conversación vieja en Antigravity podría cambiar `lastModifiedTime` y marcar 10 s de "trabajando". Se mira en vivo en la tarea 14.

## Tarea 5: máquina de estados y modelo de la tarjeta
- RED: 35 tests fallaron. GREEN: 102/102 en toda la suite.
- Revisado contra el spec ("Estados y prioridad"): el orden es durmiendo > te necesita > trabajando > listo > reposo. Una app cerrada no puede estar trabajando ni "lista" (Review Focus 4, con test). Los límites viejos se tratan como sin dato en los anillos, el sudor, la cara de agotado y la tarjeta (Review Focus 2).
- Decisión: si la lectura de Claude falla pero el último dato tiene menos de 15 min, la tarjeta sigue mostrando las filas junto con el aviso de error. Es un dato real y reciente, no uno viejo.
- Decisión: un reinicio que ya pasó se muestra como "reiniciando", no como "se reinicia en ya".
- Una sola fuente de verdad para la caducidad de los datos: `PetStateMachine.Fresh`, que también usa la tarjeta.

## Tarea 6: instalador de hooks de Claude
- RED: 19 tests fallaron. GREEN: 121/121 en toda la suite.
- Encontrado en la revisión, antes del código: el serializador JSON de .NET escapa por defecto los acentos y `< > &` (`canción` → `canción`), lo que habría ensuciado el `settings.json` del usuario. Se usa `UnsafeRelaxedJsonEscaping`; el test `Install_PreservesNonAsciiText` lo cubre.
- Revisado (Review Focus 3): un JSON con comentarios, con comas sobrantes o cuya raíz no es un objeto se rechaza sin escribir nada ni crear el backup. Si `hooks` o un evento tienen un tipo inesperado, no se pisan: se rechaza el archivo.
- Revisado: la desinstalación solo borra grupos o eventos que quedaron vacíos por nuestra culpa; un array vacío que el usuario ya tuviera se queda. Si no hay nada que quitar, el texto se devuelve idéntico, byte a byte.
- Nuevo `AtomicFile` (temporal + `File.Move`), reutilizable para credenciales y ajustes; no deja temporales (test).
- Pendiente para la tarea 13: envolver `EnsureHooks` en un try/catch de `IOException` y `UnauthorizedAccessException` para que un archivo bloqueado no tumbe el arranque. Además, comparar el `settings.json` real antes y después (solo debe cambiar `hooks`).

## Tarea 7: credenciales, cliente de uso de Claude y programación de sondeos
- RED: 26 tests fallaron. GREEN: 148/148 en toda la suite.
- Encontrado en la auditoría: si `.credentials.json` es de solo lectura o está bloqueado, guardar el token renovado lanzaba `UnauthorizedAccessException` sin capturar, y eso habría roto el bucle del coordinador. Arreglado: ahora es un error normal. Test `Client_CredentialsFileReadOnly_ErrorNotCrash`, que primero falló y luego pasó.
- Diseño de los tests: había empezado un "probe" que añadía al cliente un método solo para tests. Lo descarté: el test usa un handler con estado y el mismo cliente, sin código de test en producción.
- Revisado, seguridad: los tokens no se imprimen ni se registran en ningún sitio. Al guardar solo cambian 3 campos y la escritura es atómica. Si el refresco falla, no se reintenta en bucle hasta que el archivo cambie (después de /login).
- Revisado, la API: con 429 se dobla la espera (10, 20, 30, 30 min) y se respeta `Retry-After`. Un `Stop` no acorta el backoff. Después de un `Stop` se respeta un mínimo de 60 s entre llamadas.
- Riesgo abierto: el formato del POST de refresco (JSON con `client_id`) está tomado del binario de Claude Code, pero no lo hemos probado contra el servidor. Se verifica en la tarea 14.

## Tarea 8: servidor de hooks y comprobación de procesos
- RED: 7 tests fallaron. GREEN: 155/155; los tests del servidor, repetidos 3 veces, salen estables.
- Revisado, robustez: basura, conexiones cortadas, clientes que no envían nada (no bloquean a los demás porque cada conexión va por separado, con un timeout de 2 s), cuerpos de más de 1 MB (413) y un consumidor que lanza excepciones. Ninguno tumba el servidor (todos con test).
- Revisado, seguridad: escucha solo en `127.0.0.1` con `ExclusiveAddressUse`, así que otro programa no puede compartir el puerto. Un proceso local podría mandar eventos falsos, pero solo cambiarían la animación del dragón. Riesgo aceptado.
- Revisado, Claude Code: se responde antes de procesar el evento, así que el hook nunca hace esperar a Claude.
- Revisado, recursos: `ProcessCheck` libera cada objeto `Process`, para no perder handles en un sondeo que corre cada 5 s todo el día.

## Tarea 9: cliente de Antigravity (Windows)
- Verificación en vivo con `DragonPet.exe --probe-ag`: encuentra 1 language server, lee la cuota real (plan Pro, Gemini 5 h al 7,5 %) y la actividad ("Working" mientras Antigravity revisaba el código).
- Dato confirmado en vivo con el registro de estados: mientras trabaja, el estado es `CASCADE_RUN_STATUS_RUNNING`, pero entre fases vuelve unos segundos a `IDLE` aunque la interfaz siga diciendo "Working". El respaldo por `lastModifiedTime` (10 s) tapa esos huecos, así que el dragón no parpadea a reposo.
- Encontrado en la auditoría: si Antigravity está abierto pero su servidor aún no responde, cada sondeo (cada 3 s) repetía la consulta WMI, que es cara. Arreglado: el descubrimiento se hace como mucho una vez cada 15 s, salvo justo después de un fallo con un endpoint que funcionaba.
- Encontrado: en WPF, `System.IO` no está entre los usings implícitos. Añadido.
- Revisado, seguridad: se acepta el certificado autofirmado solo porque todas las URLs se construyen hacia `127.0.0.1`. El token CSRF no se imprime en la salida de diagnóstico.
- Decisión: `LanguageServerLocator.FindAll()` devuelve una lista en vez de un solo endpoint, porque puede haber varios language servers.

## Tarea 10: dibujo del dragón
- Verificación visual: `DragonPet.exe --render-states out/states.png` genera una hoja con 9 estados sobre fondo claro y oscuro. Coincide con los bocetos aprobados (chibi B, colores del spec, lentes, laptop, burbujas, z z, fusión bicolor con destellos, sudor y cara cansada). Se ve bien en ambos fondos.
- Diseño para gastar poco: la escena se graba una sola vez por cambio de estado (`Render`) y cada fotograma solo cambia ~15 propiedades de transforms y brushes ya creados (`Animate`). No se crea ningún objeto por fotograma; la geometría del arco se crea solo cuando cambian los anillos.
- Revisado: el timer se detiene si el dragón no es visible y cambia de 20 a 10 o 4 fps según la pose.
- Decisión: `DragonRenderer` es una instancia (el plan decía un `Draw` estático) porque necesita guardar sus transforms animables.
- Menor, sin cambio: los cuernos color crema se distinguen poco sobre fondos muy claros. Se acepta: forman parte del diseño aprobado.

## Revisión independiente del núcleo (Antigravity, Gemini 4 Neon Extra)
Informe completo en `help-by-AG/review-core.md`: 19 hallazgos. Cada uno se comprobó contra el código antes de aceptarlo. 27 tests nuevos fallaron primero (RED) y después pasaron. Suite: 187/187, estable en 3 repeticiones.

Arreglados:
- #1 Un 5xx o 429 al renovar el token se trataba como "inicia sesión". Ahora solo un 400 o 401 del refresco significa sesión inválida; lo demás es un error temporal y se reintenta.
- #2 Si fallaba guardar el token renovado (rotado), se perdía, y eso podía cerrar la sesión de Claude Code. Ahora el token renovado se guarda en memoria y se escribe en cuanto el archivo lo permite. `AtomicFile` reintenta los bloqueos breves.
- #3 Carrera con Claude Code al renovar a la vez: antes de rendirse se relee el archivo y se usan los tokens nuevos si existen.
- #4 Orden de eventos: el evento se procesa antes de responder (Claude Code espera la respuesta antes de mandar el siguiente) y una conexión nunca bloquea al bucle que acepta. El test de orden no pudo fallar de forma determinista contra el código anterior, porque una lectura síncrona serializaba por casualidad; queda como test de regresión.
- #6 `CardModel` y `CardSection` no tenían igualdad por valor (las listas se comparaban por referencia), así que la tarjeta se habría republicado cada segundo. Arreglado.
- #7 Un JSON válido pero raro (`"Stop":["x"]`, `"userStatus":[]`) lanzaba `InvalidOperationException`. Ahora se hace pattern matching antes de indexar, con el helper `JsonRead.Path`.
- #8 El servidor respondía 413/411/431 en lugar de 200. Ahora siempre responde 200. Un cuerpo de más de 1 MB se drena y se entregan los campos esenciales (evento, sesión, herramienta).
- #9 Un archivo de credenciales bloqueado se trataba como "sin credenciales" para siempre; ahora es un error temporal. Un `expiresAt` o `expires_in` absurdo ya no lanza excepción.
- #10 "NaN" e "Infinity" en la API ya no llegan a los anillos.
- #11 La tarjeta de Claude ya no queda en blanco al despertar la PC ("Sin datos recientes"), y "Actualizado hace…" ignora a Antigravity si está cerrado.
- #12 `AtomicFile` usa un temporal único, `File.Replace` (conserva los permisos), flush a disco y un borrado protegido.
- #13 La primera conversación de Antigravity (cuando la base es `null`) ya se detecta.
- #15 Un CSRF entrecomillado entero o vacío se lee bien.
- #16 Si el reloj del sistema retrocede, el dragón ya no se queda "listo" varios minutos.
- #17 `ClaudeCredentials.ToString()` ya no muestra los tokens.
- #18 El spec ahora dice `/dragonpet`, igual que el código.
- #19 Tests añadidos: cuerpo de más de 16 KB, desinstalar el hook de tipo comando, notificación antigua por mensaje, reinicio ya pasado, cancelación del llamador.

Rechazados o aplazados:
- #5 (rechazado con datos): Gemini proponía apagar el respaldo por `lastModifiedTime` después de ver `RUNNING`. El registro en vivo, tomado mientras el propio Gemini trabajaba, mostró huecos `IDLE` de unos 7 s en mitad de la tarea. Sin el respaldo, el dragón celebraría "listo" en falso. Coste aceptado: "listo" llega hasta unos 10 s tarde.
- #14 (aplazado, menor): un evento podría llegar después de `DisposeAsync`, pero solo al cerrar la app y sin efecto. Para las cabeceras ya se alquila el búfer de `ArrayPool`.
- `Transfer-Encoding: chunked` (aplazado): Claude Code manda `Content-Length`. Aun así, ahora se responde 200.

## Tareas 11 a 13: bandeja, ventanas, coordinador y arranque
- Tests: `WindowPlacement` (RED 9, GREEN), aviso extra en la tarjeta (RED por compilación, GREEN). Suite: 197/197.
- Verificación en vivo con `--log` (solo estados y tiempos, sin tokens ni contenido de conversaciones):
  - Esta misma sesión de Claude Code manda hooks a la mascota: piensa → escribe → piensa (naranja con lentes).
  - Con Antigravity revisando código a la vez: `Fusion Typing` (bicolor).
  - Antigravity solo: `Antigravity Typing/Thinking` alternando.
  - Datos reales en la tarjeta: Antigravity 12 %; Claude "Inicia sesión" (el token caducó en junio).
- **Problema encontrado en vivo (importante):** con la mascota cerrada, cada hook HTTP deja un `hook_non_blocking_error` (`ECONNREFUSED`) en la sesión de Claude Code. Es el anti-objetivo "errores visibles en cada mensaje".
  Arreglo: los hooks solo existen mientras la mascota corre. Se instalan al arrancar y se quitan al salir (también con `SessionEnding` de Windows). Verificado: al salir, `settings.json` queda **idéntico** al original y no aparecen más errores. Claude Code recarga los hooks en caliente; se comprobó con esta sesión.
  Alternativa descartada: hooks de tipo `command`, que habrían añadido unos 0,3 s por herramienta a Claude Code.
- **Problema encontrado en la revisión del cambio:** una segunda instancia (por ejemplo, doble clic dos veces) habría quitado al salir los hooks de la instancia principal. Arreglado: solo la instancia que posee el mutex toca los hooks. Verificado en vivo.
- **Problema encontrado al medir:** salir tardaba 4,7 s. El bucle del coordinador y el del servidor de hooks arrancaban desde el hilo de la UI y heredaban su `SynchronizationContext`, así que su trabajo (WMI, JSON, HTTP) corría en el hilo de la UI y al salir se bloqueaban mutuamente hasta el timeout. Arreglado con `Task.Run`, `ConfigureAwait(false)` y el cierre en el pool de hilos. Ahora sale en 92 ms.
- Nuevo `--quit`: cierra la instancia en marcha de forma ordenada (evento con nombre). Sirve para scripts y para el README.
- Bandeja sin WinForms (P/Invoke a `Shell_NotifyIcon`, `HICON` liberado con `DestroyIcon`, re-registro con `TaskbarCreated`) para ahorrar memoria.
- No verificado visualmente por mí: el usuario no dio permiso para capturar la ventana de la mascota. El aspecto se verificó con la hoja de estados (tarea 10) y el comportamiento con `--log`. Pendiente que el usuario lo mire.

## Segunda revisión independiente (capa de Windows, Antigravity con Gemini 4 Neon Extra)
Informe en `help-by-AG/review-app.md`: 11 hallazgos, comprobados uno por uno contra el código.
- #1 (bucles en el hilo de la UI y cierre de 4 s): **ya lo había encontrado y arreglado al medir el cierre**. Gemini llegó por su cuenta a la misma causa. Añadido: los campos compartidos con la UI son `volatile`.
- #2 Una llamada lenta de red (hasta 15–30 s) retrasaba la publicación de los hooks. Arreglado: dos bucles separados. El de sondeo (red) y el de publicación (cada 1 s, o al instante con un `SemaphoreSlim` cuando llega un hook) comparten estado bajo un `Lock`. Verificado en vivo: el hook se ve en el dragón en ~1 s. En el descubrimiento de Antigravity, cada puerto tiene ahora un timeout de 1,5 s (antes 5 s).
- #3 Varios monitores: `VirtualScreen` incluía huecos y barras de tareas. Ahora se usan las áreas de trabajo reales de cada monitor (`EnumDisplayMonitors` y `GetMonitorInfo`, convertidas a DIPs) para colocar el dragón y la tarjeta. Aplazado: el manifiesto PerMonitorV2, para monitores con DPI distinto.
- #4 Tarjeta: se mide y se coloca **antes** de mostrarse (sin destello), se recoloca al crecer con datos nuevos, se abre si el mouse ya estaba encima cuando llegan los primeros datos, y las filas usan un `Grid` con "…" en la etiqueta (ancho 300) para que el porcentaje y la cuenta atrás no se corten.
- #5 Con `NeedsLogin` o `NoCredentials` se borran los límites de Claude, así que el anillo deja de mostrar el número viejo.
- #6 Alt+F4 sobre el dragón cerraba la ventana para siempre y "Mostrar dragón" la rompía. Ahora `Closing` la oculta, salvo al salir de la app, y la bandeja se sincroniza. `DragMove` está protegido contra clics muy rápidos.
- #7 Bandeja: el `SetForegroundWindow` usa la ventana de mensajes si el dragón está oculto, `Tooltip` no actúa después de `Dispose` y `TaskbarCreated` se ignora si vale 0.
- #8 Cliente de Antigravity: solo acepta respuestas JSON, olvida el endpoint si la respuesta no se puede leer y valida el certificado por `SslStream.TargetHostName == "127.0.0.1"`, no por un campo mutable. Verificado en vivo con `--probe-ag`.
- #9 Si una directiva bloquea el registro, "Iniciar con Windows" no tumba la app: se revierte la casilla. Eliminado el campo muerto `StartWithWindows`.
- #10 Tres `RectangleGeometry` sin congelar, ahora congeladas. El intervalo del timer solo se reasigna si cambia.
- #11 Comprobación de `size <= 0` en `GetExtendedTcpTable`. Aplazado: soporte de 32 bits (se publica solo para x64).
- No aplicado, con razón: añadir `WS_EX_NOACTIVATE` al dragón para no robar el foco al hacer clic. No puedo verificar en vivo que el arrastre siga funcionando con ese estilo (no tengo permiso para controlar la ventana), y un dragón que no se arrastra es peor que un cambio de foco al arrastrarlo.

## Recursos y otros hallazgos de la verificación en vivo
- Medición en el peor caso (animando a 20 fps con Claude y Antigravity trabajando):
  - Antes: memoria privada 64 MB, working set 130 MB, CPU 0,39 % del equipo.
  - Después de forzar el dibujo por software (`RenderMode.SoftwareOnly`, que evita cargar Direct3D y los drivers de la GPU) y de usar `GC ConserveMemory`: **memoria privada 44 MB, CPU 0,17 %**. El working set (~100 MB) incluye librerías compartidas de .NET, WPF y Windows.
- El refresco de token ahora envía `scope`, igual que Claude Code. Lo comprobé en el binario de Claude Code: JSON con `grant_type`, `refresh_token`, `client_id` y `scope`. Hay test.

## Cierre
- Medición final (versión Release, animando con actividad real): **memoria privada 44,7 MB de media (máx. 50,6), CPU 0,20 % del equipo**, 22 hilos. Objetivo (< 60 MB, < 1 %) cumplido.
- Ciclo completo verificado en vivo: al arrancar instala 8 hooks; los hooks se reflejan en el dragón; con `--quit` la salida tarda 86 ms y `settings.json` queda idéntico al original.
- Último ajuste de la revisión final: al salir se detiene primero el servidor de hooks (la fuente de eventos) y luego el coordinador.
- Pendiente del usuario: iniciar sesión en Claude Code (`claude` y luego `/login`) para ver el anillo naranja, y mirar el dragón en pantalla (yo no tuve permiso para capturarlo): arrastrarlo, pasar el mouse y usar el menú de la bandeja.

## Incidencia: "cerré Claude y el dragón se cerró"
- Diagnóstico con evidencia:
  - No hubo crash (ningún error de DragonPet en el visor de eventos de Windows).
  - Los 8 hooks seguían en `settings.json`, señal de una muerte abrupta sin pasar por la salida ordenada.
  - Los procesos que lanza Claude Code viven en un job de Windows; Claude termina ese grupo al salir. Yo lancé el dragón desde ahí, así que murió con Claude.
- Medido con `QueryInformationJobObject`:
  - Terminal de Claude: job `0x800` (permite breakaway).
  - Apps abiertas desde el Explorador y procesos creados por WMI: job `0x1800`.
  - Primer arreglo (`CREATE_BREAKAWAY_FROM_JOB`): el dragón salía del job interior, pero quedaba en un job exterior `0x0` que no coincide con el del Explorador. Muy probablemente era de la app de Claude, así que **lo descarté** midiendo, no suponiendo.
- Arreglo final (`JobEscape`): si el dragón arranca dentro de un job, se relanza una vez vía WMI `Win32_Process.Create` (breakaway como plan B). `--detached` evita bucles.
- Verificado en vivo: lanzado desde Claude, el dragón queda con padre `WmiPrvSE.exe` y job `0x1800` (igual que una app del Explorador); su ventana es visible en el escritorio del usuario y tiene su icono de bandeja; `--quit` sigue cerrando bien y quitando los hooks.
- Dato real de Claude por primera vez (el usuario ya inició sesión): sesión de 5 h al 80 %, con sudor. Confirma que `utilization` viene en escala 0–100.
- Pendiente de confirmar por el usuario: cerrar Claude y ver que el dragón sigue vivo (en "Claude: cerrado").

## Antigravity cerrado se veía "en reposo" y transición gradual de colores
- **Causa medida:** al cerrar Antigravity quedan 2 procesos `Antigravity.exe` sin ventanas. Son *sidecars* (el exe usado como runtime: `--import=…sidecar_sdk… main.mjs`, padre `cmd.exe`) que sobreviven a la app. Su `language_server` sí se cierra con la app.
- **Arreglo:** "Antigravity abierto" ahora significa que existe su `language_server` (ruta con `\antigravity\`), que vive lo mismo que la app y es de donde se leen los datos. Nuevo `ProcessCheck.AnyRunningFrom(name, pathFragment)` con su test (RED → GREEN).
- Verificado en vivo, leyendo el log desde fuera del terminal: con los 2 sidecars vivos, el dragón marca "Antigravity: cerrado" y el anillo azul en gris.
- **Transición de colores:** nuevo `ColorTransition` en Core (smoothstep, 0,6 s). El primer objetivo se aplica al instante, un nuevo objetivo arranca desde el color visible (sin saltos), los extremos son exactos y no asigna memoria por fotograma. 15 tests.
  - El dragón dibuja siempre dos mitades con pinceles vivos (iguales salvo en la fusión), así que cualquier cambio, incluido entrar o salir de la fusión, se mezcla.
  - Durante la mezcla la vista sube a 20 fps, aunque esté durmiendo a 4 fps.
- **Encontrado en mi revisión visual (`--render-transitions`):** mezclar en RGB llevaba el menta al naranja pasando por un oliva turbio. Ahora se mezcla en OKLCh (luminosidad, croma y tono por el camino corto): menta → lima → dorado → naranja, y naranja → rosa → violeta → azul. Test `MintToOrange_MidpointStaysVivid` (RED con RGB, GREEN con OKLCh).
- **Hallazgo de diagnóstico:** este terminal de Claude ve una copia aislada de `%LOCALAPPDATA%\DragonPet`, así que lo que escribe el lanzador tapa lo que escribe el dragón real (que corre fuera). Para leer el log real hay que copiarlo desde un proceso externo.
- Suite: 213/213.

## Animaciones vivas: transiciones suaves y gestos (addendum `2026-11-19-dragon-animations-design.md`)
- Núcleo con TDD:
  - `Motion` (suavizado exponencial, campana, rebote, ventanas).
  - `GestureScheduler`, con tablas por estado, pausas aleatorias y AFK a partir de 120 s. Es determinista con semilla y corta el gesto al cambiar de estado.
  - 25 tests RED → GREEN. Suite: 238/238.
  - Encontrado: un test de pausas fallaba porque la simulación de 600 s entraba en AFK, donde las pausas son más cortas a propósito. Era un error del test: se acotó a los primeros 120 s.
- Renderizador reescrito como escena retenida de `DrawingGroup` construida una vez. Cada fotograma solo cambia opacidades, transformaciones y colores:
  - lentes, laptop, burbujas, "!", "z z", sudor y destellos con entrada y salida;
  - ojos y bocas que se funden;
  - brazo que se levanta y saluda;
  - fases acumuladas, para que el ritmo cambie sin saltos;
  - squash and stretch al cambiar de estado.
- La vista sube a 20 fps solo con movimiento (`NeedsFastFrames`).
- Revisión visual con `--render-gestures` (fotogramas de 13 gestos y 4 transiciones). Encontrado y arreglado:
  - La llama del eructo era pequeña y subía **encima del ojo**: ahora sale de lado, más grande, y termina en humo.
  - La burbuja de ronquido era pequeña: ahora es un 50 % más grande.
  - El humito dormido era casi invisible (gris sobre gris): ahora son nubecitas blancas con contorno que se abren hacia los lados de la cabeza.
- Arreglado en mi revisión: un gesto cortado a medias por un cambio de estado desaparecía de golpe. Ahora queda congelado y se desvanece en ~0,15 s (todos sus efectos se multiplican por un peso de gesto).

## Tercera revisión independiente: animación (Antigravity, Gemini 4 Neon Extra)
Informe en `help-by-AG/review-anim.md`: 10 hallazgos, comprobados contra el código. El #2 (gesto cortado de golpe) ya estaba arreglado por mí mientras Gemini revisaba la versión anterior: coincidencia independiente. Aplicado:
- #1 El primer fotograma después de un cambio de estado reutilizaba el `dt` del tick lento (hasta 0,25 s) y los fundidos saltaban hasta un 74 %. Ahora los fundidos avanzan como mucho un paso de 0,05 s.
- #3 `GestureScheduler`: al volver de ocultar el dragón o de suspender la PC encadenaba el siguiente gesto sin pausa. Ahora la pausa se cuenta desde `now`; además ignora relojes no finitos y el progreso queda acotado a 0–1. Tests RED → GREEN: `AfterLongClockJump_…`, `NonFiniteClock_…`, `Progress_StaysWithin0And1_…`.
- #4 Rendimiento:
  - los grupos invisibles ya no se tocan;
  - los setters solo escriben si el valor cambia (sin boxing ni notificaciones);
  - los destellos titilan con su pincel en vez de con la opacidad del grupo, que forzaba una capa offscreen;
  - la mitad derecha del cuerpo no se dibuja si las dos mitades tienen el mismo color.
- #5 Un cambio solo de anillos (un sondeo de cuota) ya no baja los fps en mitad de un gesto, y solo esos cambios vuelven a grabar la escena.
- #6 Con el dragón agotado, la boca ondulada se dibujaba encima del bostezo o del eructo (dos bocas). Además, el parpadeo saltaba al cambiar de pose: ahora parpadea siempre y los ojos abiertos se funden solos.
- #7 Las mejillas compartían un `ScaleTransform` y se salían de la cara al inflarse. Ahora cada una escala sobre su centro (verificado a ojo).
- #8 `Motion` y `ColorTransition` a prueba de NaN (test `NonFiniteInputs_…` y `OddDurations_…`, RED → GREEN). Los canales que no cambian se saltan el cálculo OKLCh.
- #9 Calidad, verificada en fotogramas:
  - humo más visible;
  - saltito con agachada, impulso y aterrizaje;
  - murmullo que abre la boca y ladea la cabeza;
  - mirar arriba con lentes sin choque;
  - cola ±20°;
  - burbuja que sale de la fosa nasal real y explota sin tiempo muerto;
  - brazo que gira desde el hombro.
- #10 Tests añadidos: independencia de los fps, que aparezcan todos los gestos permitidos, que el AFK se reinicie al volver a reposo, `DurationOf` > 0, cruce de tono por el camino corto, canal sin cambio exacto y uso antes del primer objetivo.
- Encontrado en mi verificación visual posterior: el brazo levantado de "te necesita" no se leía (mismo color que el ala, debajo). Ahora sube más y muestra una palma clara. Verificado a ojo en fondo claro y oscuro.
- Suite: 256/256. Build sin warnings.
- Medición final (Release, animando mientras Claude trabaja): memoria privada 44,7 MB, **CPU 0,14 %**. Antes de los arreglos de rendimiento de la revisión, con el sistema de animación nuevo, era 0,32 %.

## Bug: "¿por qué está pensando?" (dragón en "pensando" con Claude ya parado)
- Depuración sistemática, con evidencia antes de arreglar:
  1. Añadí al `--log` cada hook recibido (evento, sesión abreviada, herramienta y si viene de un subagente), sin contenido.
  2. Prueba controlada con `claude -p` por terminal: llegan `UserPromptSubmit → Stop → SessionEnd`. `Stop` funciona.
  3. En el historial de la sesión de escritorio, `stop_hook_summary` muestra que nuestro hook de `Stop` **sí** corrió al terminar la respuesta, sin errores. Hipótesis "no llega Stop" descartada.
  4. En el Claude Code de la app (2.1.325): después de cada respuesta se lanza un fork en segundo plano (`querySource:"prompt_suggestion"`) que genera la sugerencia del siguiente mensaje (el "haz el commit" de la caja de texto). Al terminar dispara `SubagentStop`, con el mismo `session_id` más `agent_id`.
  5. Mi tracker mapeaba `SubagentStop` a "pensando", así que el dragón volvía a pensar justo después del `Stop` y se quedaba así hasta caducar (15 min). Encaja con la captura: "pensando", sin laptop.
- Arreglo de raíz: `SubagentStop` y cualquier evento con `agent_id` se ignoran, porque no son la conversación principal (un subagente real ya queda cubierto por `PreToolUse` y `PostToolUse` de la herramienta `Agent`). Tests RED → GREEN: `BackgroundForkAfterStop_StaysIdle`, `EventsFromSubagents_AreIgnored`, `SubagentStop_DoesNotChangeTheMainSession`. Suite: 258/258.
- Verificación en vivo pendiente: el dragón queda corriendo con `--log`. Al terminar esta respuesta, el log debe mostrar `Stop` y luego `SubagentStop subagent=yes`, con el dragón en reposo.
- Verificado en vivo después: el log mostró `Stop` → dragón contento → reposo, y el `SubagentStop subagent=yes` posterior se ignoró. El dragón ya no se queda pensando.

## Preparación para GitHub
- Decisiones del usuario: licencia MIT, repositorio público, README solo en inglés y publicar toda la documentación (`docs/` y `help-by-AG/`).
- `LICENSE` MIT a nombre de "DragonPet contributors", para no publicar datos personales. `README.md` reescrito en inglés con imágenes generadas por `--render-states` y `--render-gestures`.
- Revisión de privacidad de todos los archivos que se publicarán: sin correos, nombres, tokens ni rutas con datos personales fuera de los ejemplos genéricos.

## Cuarta revisión independiente: prelanzamiento (Antigravity, Gemini 4 Neon Extra)
Informe en `help-by-AG/review-release.md`: 8 hallazgos, comprobados contra el código. Gemini editó su propuesta de test de sesiones unos segundos después de entregarla; el cambio (comprobar que se expulsa la sesión **más antigua**, no solo contar) mejora el test y lo adopté. Aplicado, con tests RED → GREEN en `ReleaseHardeningTests`:
- #3 `HookServer`:
  - solo acepta `POST /dragonpet` sin cabecera `Origin` y con `Host` loopback, para que una página web abierta en el navegador no pueda enviar eventos falsos (CSRF o DNS rebinding);
  - como mucho 16 conexiones a la vez;
  - el resumen de cuerpos de más de 1 MB conserva `agent_id` (sin él volvía el bug de "pensando" con subagentes de salida enorme);
  - decodifica con reglas JSON en vez de `Regex.Unescape`, que lanzaba excepción con un `\uZZZZ`.
- `ClaudeActivityTracker`: como mucho 64 sesiones; al llegar una nueva, olvida la menos reciente.
- Encontrado en mi verificación: el test de peticiones ajenas fallaba de vez en cuando. Causa: al rechazar una petición, el servidor cerraba sin leer el cuerpo, así que Windows mandaba un RST y el cliente podía perder su `200`. Ahora el cuerpo se lee y se descarta antes de responder. Suite estable en 5 ejecuciones seguidas.
- #2 Detección de Antigravity:
  - acepta cualquier `language_server*` en una ruta con `antigravity`, o un `Antigravity.exe` con ventana (los sidecars no tienen), para cubrir otras versiones y carpetas de instalación;
  - el localizador prefiere el servidor de Antigravity si hay otro editor basado en Codeium abierto, y no repite puertos;
  - `AnyRunningFrom`, que ya no se usaba, se eliminó;
  - `AnyRunningWithWindow` no tiene test unitario (el proceso de test no tiene ventana) y se verificó en vivo.
- #7 Ya no se instala el hook `SubagentStop`, que siempre se ignoraba. `Uninstall` sigue quitando el de versiones anteriores (test).
- #1 `JobEscape`:
  - reenvía solo `--log`;
  - no relanza si corre como `dotnet DragonPet.dll`;
  - `ReturnValue` nulo ya no lanza excepción;
  - `CreateProcess` recibe la ruta del exe;
  - la app comprueba el mutex antes de relanzar, así que una segunda instancia ya no dispara WMI.
  - `Quote` pasó a `Core` (`WindowsCommandLine.Quote`) con un test de ida y vuelta contra el `CommandLineToArgvW` real de Windows: barras finales, comillas tras barra, tabuladores y cadena vacía.
- #4 Sin `~/.claude` (PC sin Claude Code) ya no crea `~/.claude/settings.json` al arrancar ni lo deja con `{}` al salir.
- #5 Las hojas `--render-*` crean su carpeta. `.gitignore` añade `*.suo`, `.vscode/`, `.idea/`, `*.log`, `*.bak` y `*.tmp`. README (en inglés, adaptado; el parche de Gemini apuntaba al README anterior en español): listener local y sus reglas, WMI, certificado solo para `127.0.0.1`, `state.log` y sección de desinstalación.
- #6 Resiliencia:
  - al cambiar la configuración de pantallas (por ejemplo, desconectar un monitor) el dragón se recoloca;
  - el `RegisteredWaitHandle` se libera al salir;
  - al cerrar sesión de Windows se permite cerrar la ventana;
  - `Log` ignora errores de E/S;
  - `--render-states` con un número inválido usa el valor por defecto;
  - `Wake` tolera el cierre.
- Suite: 274/274. Build sin warnings.
- Verificación en vivo (Release):
  - relanzado fuera del job por WMI con línea `--detached --log`;
  - `settings.json` con 7 eventos y sin `SubagentStop`;
  - los hooks reales de Claude Code pasan el filtro (`PostToolUse` lleva a pensando y `PreToolUse` a escribir);
  - una petición con `Origin: https://evil.example` recibe `200` y no llega al dragón;
  - Antigravity se detecta abierto (22 %).
- Medición: CPU 0,16 % trabajando, memoria privada 43,2 MB. El dragón quedó corriendo sin `--log`.

## Cambio: solo se muestran las apps abiertas
- Pedido del usuario: no mostrar anillos grises de apps cerradas, y que la tarjeta muestre solo las apps abiertas. Motivo: con la app cerrada en esta PC, su límite puede gastarse desde el celular y el dragón mostraría un porcentaje viejo.
- `PetVisual` cambia `RingsDimmed` por `ClaudeRingShown` y `AgRingShown` (app abierta).
- Los límites de una app cerrada ya no cuentan para el sudor ni para "agotado".
- Durmiendo no hay anillos. Una app abierta sin datos todavía muestra su anillo gris y vacío.
- Tarjeta: solo las secciones de apps abiertas. El color va por nombre de sección y no por posición. Sin apps abiertas muestra "Abre Claude o Antigravity para ver tus límites". "Actualizado hace…" solo cuenta apps abiertas. El tooltip de la bandeja también solo lista apps abiertas.
- Con Claude cerrado ya no se consulta su uso (menos peticiones). Al abrirlo, `ClaudePollScheduler.OnAppOpened` pide datos enseguida si los últimos tienen más de 1 min; hasta que llegan, el anillo queda gris ("Cargando…"). Respeta la espera por límite de peticiones y la espera de login.
- Los anillos aparecen y desaparecen con un fundido (~0,3 s, como los accesorios). El que se va conserva su último valor mientras se desvanece. La vista solo se redibuja durante el fundido.
- TDD: 12 tests RED → GREEN en `ClosedAppsTests` (máquina de estados, tarjeta y programador). Ajustados `NothingOpen_Sleeping` y `BothOpenIdle_Mint`. Eliminado `Card_AgClosed`, sustituido por `Card_AgClosed_HasNoAgSection`. Suite: 286/286. Build sin warnings.
- Verificación visual con `--render-states`: dormido sin anillos, solo naranja con solo Claude abierto, solo azul con solo Antigravity y los dos con ambas abiertas. Fila dormir de `--render-gestures`: los anillos se desvanecen gradualmente. Se regeneraron `docs/images/states.png` y `gestures.png`.
- En vivo (Release): arranca dormido y sin anillos, detecta las dos apps abiertas, muestra los anillos grises un momento y luego Claude 54 % y Antigravity 83 %.
- Sin verificar en vivo: cerrar una app con el dragón corriendo. Claude no se puede cerrar desde esta sesión y Antigravity estaba en uso. Ese caso lo cubren los tests y las hojas de render.
- Límite conocido: si Claude se reabre durante una espera por límite de peticiones, se muestra el último dato si tiene menos de 15 min, hasta que termine la espera.

## Cambio de nombre: DragonPet → Hoardling
- El usuario eligió **Hoardling** entre las propuestas. Los dragones guardan su tesoro (*hoard*) y este guarda tus límites; el nombre no nombra ninguna herramienta, para la idea de compañero universal.
- Renombrado mecánico primero, con la suite en verde antes de cambiar comportamiento (286/286):
  - solución, proyectos y carpetas: `Hoardling.sln`, `src/Hoardling`, `src/Hoardling.Core`, `tests/Hoardling.Core.Tests`;
  - espacios de nombres, `Hoardling.exe`, mutex y evento de cierre, ventana y bandeja, User-Agent y carpetas de configuración y log.
- Cambios de comportamiento con TDD (18 tests RED → GREEN; suite 292/292, build sin warnings):
  - los hooks pasan a `/hoardling` y `HookServer.Path` es la única fuente de la URL;
  - una petición a `/dragonpet` ya no se entrega;
  - `Install` sustituye los hooks que dejó el nombre anterior (por ejemplo, si el dragón viejo se cerró de golpe) en vez de duplicarlos;
  - `Uninstall` quita los dos;
  - solo cuentan como instalados los hooks nuevos;
  - la copia de seguridad pasa a `.hoardling.bak`.
- Migración en el PC (`LegacyMigration`, solo en la instancia principal y sin romper nada si falla):
  - mueve `%APPDATA%\DragonPet\settings.json` a `%APPDATA%\Hoardling` y borra la carpeta vieja si queda vacía;
  - cambia el valor `DragonPet` de "Iniciar con Windows" (apuntaba a un exe que ya no existe) por `Hoardling`;
  - no escribe la clave Run si se ejecuta como `dotnet Hoardling.dll`.
- Verificación en vivo:
  - `DragonPet.exe --quit` quitó sus hooks;
  - `Hoardling.exe` se relanzó fuera del job;
  - la clave Run quedó solo con `Hoardling`;
  - los ajustes se movieron con la posición del dragón;
  - `settings.json` quedó con 7 eventos hacia `/hoardling`;
  - los hooks reales de Claude Code llegan (`PostToolUse` lleva a pensando y `PreToolUse` a escribir);
  - con Antigravity cerrado, el log muestra `ag=hidden`: primera verificación en vivo del anillo oculto de una app cerrada;
  - memoria privada 41,6 MB.
- Se mantiene, a propósito:
  - el historial (auditorías, specs, plan y las revisiones de Gemini en `help-by-AG/`) conserva "DragonPet", con una nota al inicio de cada documento;
  - quedan en el PC del usuario `%LOCALAPPDATA%\DragonPet\state.log` (log viejo) y `~/.claude/settings.json.dragonpet.bak` (copia de sus ajustes de Claude previa a la mascota). Hay que avisarle; no se borran sin su permiso.

## Publicación en GitHub

- Restos de DragonPet en el PC del usuario: con su permiso se mandaron a la Papelera de reciclaje (no se borraron para siempre).
  - `%LOCALAPPDATA%\DragonPet` (solo tenía el `state.log` viejo).
  - `~/.claude/settings.json.dragonpet.bak`: antes se comprobó que tenía los mismos ajustes que `settings.json.hoardling.bak` y ningún hook de la mascota, así que no se pierde nada.
- README: repaso de tono a pedido del usuario, para que se lea como escrito por una persona y no como un manual. Se mantuvo la precisión donde hace falta (qué toca en el PC, fuentes de datos, línea de comandos). Se añadieron el porqué del proyecto y la idea del compañero universal.
- Antes de publicar:
  - barrido de privacidad de los 83 archivos: sin correos, sin rutas con el nombre de usuario, sin tokens ni JWT y sin metadatos en las imágenes; el único ID es el `client_id` público de Claude Code;
  - `dist/`, `bin/`, `obj/` y `.superpowers/` fuera del repositorio;
  - build Release sin warnings y 292/292 tests en verde.
- Identidad de git: solo en este repositorio, con el usuario de GitHub y su correo `noreply`, para no publicar el correo personal.
- Gemini como coautor, a pedido del usuario: el commit lleva también `Co-Authored-By: Gemini` con el correo `noreply` de `gemini-code-assist[bot]`, la cuenta de Google para Gemini en GitHub (Antigravity no tiene una propia). Se descartó un correo de un dominio de terceros que circula para esto. El commit se reescribió y se volvió a subir con `--force-with-lease` minutos después de publicarlo, con 0 forks.
- Release v1.0.0: `Hoardling.exe` de un solo archivo (unos 680 KB; necesita el .NET 10 Desktop Runtime), compilado en `out/release` para no tocar el exe que está corriendo. Prueba de humo: `--render-states` dibuja todos los estados igual que la versión en uso.

## Arreglo: Antigravity 2.34 dejó de mostrar sus datos

- Síntoma: tras actualizar Antigravity, el anillo azul y la actividad dejaron de llegar.
- Causa raíz, comprobada en el PC del usuario: Antigravity 2.34.0 arranca el servidor de lenguaje en modo `--standalone` (`resources\bin\language_server.exe`, `--https_server_port 0`, `--read_host_bridge_token_from_stdin`) y ya no pone `--csrf_token` en la línea de comandos. El descubrimiento por WMI no encontraba token y sin token el servidor responde `401 missing CSRF token`.
- Lo que sí hay: Antigravity escribe `~/.gemini/antigravity/daemon/hub_<puerto>.json` con `pid`, `httpsPort`, `httpPort`, `lsVersion` y `csrfToken`, para que otros programas locales lo encuentren. Con ese token, `RetrieveUserQuotaSummary`, `GetUserStatus` y `GetAllCascadeTrajectories` responden igual que antes (por HTTPS y por HTTP). Se descartaron alternativas más invasivas (leer la memoria o el entorno de otro proceso, o usar las credenciales de Google de Antigravity).
- Arreglo con TDD (16 tests RED → GREEN; suite 308/308, build sin warnings):
  - `AntigravityParser.ParseDaemonFile`: valida `pid` entero positivo, token no vacío y puertos 1–65535; HTTPS primero; un JSON roto o incompleto devuelve null en vez de fallar;
  - `AntigravityParser.PickLiveDaemons`: Antigravity nunca borra los archivos viejos (había 29), así que solo cuentan los de procesos `language_server*` vivos, del más nuevo al más viejo y uno por proceso;
  - `LanguageServerLocator.FindAll` prueba primero esos archivos (máx. 4 KB cada uno; un archivo que se está reescribiendo se salta sin perder los demás) y solo si ninguno responde cae al método viejo por WMI, de forma perezosa. Con Antigravity 2.34 el descubrimiento ya no necesita WMI ni la tabla TCP, así que es más barato.
- Verificación en vivo con Antigravity 2.34.0:
  - `--probe-ag` encuentra el pid 2800 (puertos 30287 y 30288) y lee el plan, los grupos de límites y la actividad;
  - Hoardling, relanzado con `--log`, muestra `ag=0%` (el anillo vuelve a aparecer) y el modo azul "trabajando";
  - luego se relanzó sin `--log`: 41,6 MB y los 7 hooks en su sitio.
- README: se añade la carpeta `~/.gemini/antigravity/daemon/` a "What it changes on your PC" (solo lectura) y a las fuentes de datos; WMI queda solo para versiones anteriores a 2.34.
- Se publica como v1.0.1: `<Version>1.0.1</Version>` en `Hoardling.csproj`, para que el exe diga su versión (la 1.0 salió con la 1.0.0 por defecto).

## Arreglo: usar Gemini 4 Argon no movía el anillo azul

- Síntoma: el usuario usa Argon en Antigravity y el anillo azul no cambia.
- Causa raíz, comprobada con la respuesta cruda de `RetrieveUserQuotaSummary`: en el plan Pro, Argon tiene una cuota aparte y solo semanal (`gemini-argon-wtu-weekly`, sin límite de 5 h). El anillo tomaba el máximo de los límites de 5 h de todos los grupos, así que Argon nunca contaba. El parser estaba bien: la tarjeta ya mostraba "Gemini 4 Argon · Semana". Era un caso que la regla original ("solo el límite de 5 horas, el más gastado") no contemplaba.
- Arreglo con TDD (8 tests RED → GREEN; suite 316/316, build sin warnings): `ProviderLimits.RingPercent()` toma, por cada grupo, su límite más corto (5 h si lo tiene; si no, el semanal; si no, otro) y el anillo muestra el más gastado de esos. Se mantiene la decisión del usuario para los grupos con 5 h (su semanal sigue fuera del anillo), Claude no cambia y el sudor al 80 % sigue la misma regla, ahora también con Argon. Se añadió una muestra real de la cuota con Argon usado (sin datos personales).
- Verificación en vivo: con Argon al 31,7 % semanal y Gemini 5 h al 0,5 %, el log pasó a `ag=32%` (antes habría mostrado 0,5 %). Luego se relanzó sin `--log`: 41,3 MB y los 7 hooks en su sitio.
- README: el anillo azul explica el caso de los modelos con solo límite semanal y "suda" pasa a "a ring is at 80 % or more".
- Efecto a tener en cuenta: mientras se usa otro modelo, el anillo puede seguir mostrando el uso semanal de Argon si es el más alto. Es lo que pide la regla del "más gastado"; la tarjeta muestra cada grupo por separado.
- Se publica como v1.0.2 (`<Version>1.0.2</Version>` en `Hoardling.csproj`).
