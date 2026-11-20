# Revisión independiente de `DragonPet` (capa de Windows / WPF)

Revisión de solo lectura de `src/DragonPet/**/*.cs`, `App.xaml`, `DragonPet.csproj` y `src/DragonPet.Core/WindowPlacement.cs` (+ interacción con `HookServer.cs` y `CardModel.cs`) frente al spec, el plan y `docs/superpowers/audits/audit-log.md`.

---

## Hallazgos

### 1. `PetCoordinator.Start()` y `HookServer.TryStart()` corren en el hilo de UI de WPF: congelan la interfaz y causan un deadlock de 4 s al salir
- Gravedad: Crítico
- Archivo: `src/DragonPet/Services/PetCoordinator.cs:70-103`, `194-210`; `src/DragonPet/App.xaml.cs:64`, `83`, `214-221`; `src/DragonPet.Core/HookServer.cs:48`, `59`, `76`
- Escenario: `App.OnStartup` ejecuta `_hooks.TryStart()` (línea 64) y `_coordinator.Start()` (línea 83) en el hilo de UI de WPF (`DispatcherSynchronizationContext`). Ninguno usa `Task.Run(...)` ni `.ConfigureAwait(false)` (incluso `await Task.Yield()` en `HookServer.HandleAsync:76` reencola en `DispatcherSynchronizationContext`). Como resultado:
  1. Todo `TickAsync` (`ProcessCheck.AnyRunning` cada 5 s, WMI `LanguageServerLocator.FindAll()` de ~100–300 ms, lecturas/escrituras con `FlushToDisk` de `.credentials.json`) y el procesamiento de hooks corren **en el hilo de UI**.
  2. Al cerrar la app, `App.OnExit` (en el hilo de UI) bloquea con `_coordinator.DisposeAsync().AsTask().Wait(2 s)` y luego `_hooks.DisposeAsync().AsTask().Wait(2 s)`. Las continuaciones de `await timer.WaitForNextTickAsync(ct)` y `await listener.AcceptTcpClientAsync(ct)` intentan reanudar en el hilo de UI bloqueado → **deadlock de 2 s + 2 s = 4 s en cada salida**, y `_ag.Dispose()`, `_http.Dispose()`, `_listener.Dispose()` y `_cts.Dispose()` no llegan a ejecutarse antes de salir.
  3. Además, al mover `PetCoordinator` a un hilo de fondo, `HookServerOk` y `Warning` (`PetCoordinator.cs:57-59`) se escribirán desde la UI y se leerán desde el hilo de fondo sin `volatile`.
- Por qué importa: La animación, el arrastre y el menú de la bandeja se traban durante consultas WMI/procesos, y salir desde la bandeja cuelga la app 4 segundos en cada cierre dejando recursos sin liberar limpiamente.
- Arreglo sugerido: Lanzar ambos bucles fuera del `SynchronizationContext` con `Task.Run(() => RunAsync(_cts.Token))` y `Task.Run(() => AcceptLoopAsync(listener, _cts.Token))` (y añadir `.ConfigureAwait(false)` en sus `await`s internos y `DisposeAsync`); hacer `volatile` los campos de `HookServerOk`/`Warning`; en `App.OnExit`, detener `_hooks` antes que `_coordinator` usando `Task.Run(() => ...DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(2))`.
- Test que lo demostraría: `DisposeAsync_FromCustomSynchronizationContext_DoesNotDeadlock`: instalar un `SynchronizationContext` de un solo hilo (simulando WPF), llamar a `TryStart()` / `Start()` y luego `DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(1))` afirmando que retorna `true` en < 200 ms.

### 2. Los sondeos HTTP y el descubrimiento de puertos de Antigravity bloquean `Publish()` hasta 15–30 s y retrasan los avisos de Claude Code
- Gravedad: Importante
- Archivo: `src/DragonPet/Services/PetCoordinator.cs:128-174` y `src/DragonPet/Services/AntigravityClient.cs:38`, `98-115`
- Escenario: `TickAsync` ejecuta `await _ag.GetActivityAsync`, `await _ag.GetQuotaAsync` y `await _claudeUsage.FetchAsync` (con `Timeout = 15 s`, hasta 30 s con refresh + reintento) de forma secuencial **antes** de llegar a `Publish()` (línea 173). Además, `AntigravityClient.CallAsync` prueba cada `(endpoint × puerto × ["https", "http"])` en serie con `_http.Timeout = 5 s`: si `language_server` abre 2 puertos que no responden HTTP/TLS, el bucle queda bloqueado 20 s.
- Por qué importa: Mientras un sondeo de red está esperando timeout (Wi-Fi lenta, Anthropic lento o descubrimiento de puertos), los hooks de Claude Code (`PermissionRequest` / «te necesita», `PreToolUse`, `Stop`) no se publican en la UI durante 15–30 s, la animación «Listo» de 2 s se pierde entera y la alternancia piensa/escribe de Antigravity se congela.
- Arreglo sugerido: Desacoplar los sondeos de red del tick de 1 s de `Publish()` (lanzándolos como tareas no solapadas que actualizan su estado al terminar, y despertando `Publish` inmediatamente desde `OnHookEvent`), y usar un timeout corto por puerto (~1 s) en el bucle de descubrimiento de `AntigravityClient`.
- Test que lo demostraría: `Coordinator_SlowUsageFetch_StillPublishesClaudeHookWithin200ms`: con un `HttpMessageHandler` que tarda 5 s en responder, disparar `OnHookEvent(PermissionRequest)` y comprobar que `OnPublish` recibe `PetPose.NeedsYou` en menos de 1 s.

### 3. Varios monitores y DPI distintos: `VirtualScreen()` rompe la colocación del dragón y la tarjeta (zonas muertas, barra de tareas y salto entre monitores)
- Gravedad: Importante
- Archivo: `src/DragonPet/App.xaml.cs:178-203` y `src/DragonPet/DragonPet.csproj`
- Escenario:
  1. `PlacePet()` pasa `[PrimaryWorkArea(), VirtualScreen()]` a `WindowPlacement.Resolve`. Como `VirtualScreen()` es el rectángulo envolvente total de todos los monitores **incluyendo las barras de tareas y huecos vacíos** entre monitores de distinta altura/resolución, una posición guardada en un hueco inexistente o sobre la barra de tareas (donde `VirtualScreen()` tiene mayor área de solape que `PrimaryWorkArea()`) se conserva en vez de reubicarse.
  2. `WorkAreaFor()` devuelve `VirtualScreen()` en cuanto el dragón está en un monitor secundario: al pasar el mouse cerca del borde izquierdo del monitor 2, la tarjeta salta al monitor 1; cerca del borde inferior, queda tapada por la barra de tareas del monitor 2 o cortada fuera de pantalla.
  3. Con monitores de distinto DPI (p. ej. portátil 150 % + monitor externo 100 %), `SystemParameters.WorkArea` y `VirtualScreen*` dividen por el DPI del monitor primario; además, sin un manifiesto `PerMonitorV2`, Windows escala por mapa de bits el dragón en el segundo monitor (se ve borroso/dentado, contra el anti-objetivo del spec).
- Por qué importa: Con dos monitores o escalas distintas, el dragón puede quedar oculto tras la barra de tareas o en zona invisible, la tarjeta aparecer en el monitor equivocado y los vectores verse borrosos en el monitor secundario.
- Arreglo sugerido: Obtener los `rcWork` reales de cada monitor con `EnumDisplayMonitors` + `GetMonitorInfoW` (y convertir píxeles físicos a DIPs con la matriz `TransformFromDevice` / DPI de cada monitor) tanto para `PlacePet` como para `WorkAreaFor`, y añadir un `app.manifest` con `PerMonitorV2`.
- Test que lo demostraría: Prueba manual/integración en 2 monitores (o test unitario del conversor `MonitorsToWorkAreas` con 2 rectángulos de distinto tamaño y DPI 150 % / 100 %): verificar que `WorkAreaFor` devuelve solo el `rcWork` del monitor 2 y que una posición en el hueco bajo el monitor 1 se resetea al primario.

### 4. `CardWindow`: al crecer por una actualización en vivo se sale de la pantalla, parpadea al mostrarse y recorta la cuenta atrás en filas largas
- Gravedad: Importante
- Archivo: `src/DragonPet/Windows/CardWindow.cs:42-55`, `57-79`, `99-112` y `src/DragonPet/App.xaml.cs:96-99`, `108-117`
- Escenario:
  1. Con el dragón en su posición por defecto (esquina inferior derecha), el usuario pasa el mouse mientras la tarjeta es baja (p. ej. «Cargando…» o Antigravity cerrado) → llega un `OnPublish` con filas nuevas → `App.OnPublish` llama a `_card.Update(card)` (línea 98) sin recalcular `Left`/`Top` → la tarjeta crece hacia abajo y queda cortada detrás de la barra de tareas.
  2. Si el usuario posa el mouse durante el primer segundo antes de que llegue `_latestCard` (`_hovering == true`, `_card.IsVisible == false`), `OnPublish` comprueba `_card.IsVisible` y nunca abre la tarjeta mientras el cursor sigue encima.
  3. `ShowFor` llama a `Show()` (línea 49) **antes** de asignar `Left` y `Top` (líneas 53–54), provocando un destello en `(0, 0)` o en la ubicación previa del dragón antes de moverse.
  4. En `AddSection` (líneas 99–112), el ancho interior es 240 px (`270 - 28 - 2`) y el `DockPanel` da primero todo el ancho sin ajuste a `row.Label` (p. ej. `"Gemini 4 Argon · Semana"`, ~140 px), dejando ~100 px a `right` (`"100% · se reinicia en 6d 23h"`, ~155 px), que queda recortado.
- Por qué importa: Al pasar el mouse en la esquina inferior derecha la tarjeta parpadea, puede quedar cortada bajo la barra de tareas y las cuentas atrás de grupos largos de Antigravity se truncan.
- Arreglo sugerido: Medir `_card.Measure(new Size(CardWidth, double.PositiveInfinity))` y fijar `Left`/`Top` antes de `Show()`; recolocar la tarjeta también al actualizar mientras está visible (y abrirla si `_hovering && !_card.IsVisible`); cambiar el `DockPanel` de cada fila por un `Grid` de 2 columnas (`Star` con `TextTrimming.CharacterEllipsis` para el nombre y `Auto` para `% · reinicio`) o ampliar ligeramente `CardWidth`.
- Test que lo demostraría: Verificar en vivo (o con un modo `--render-card`) una tarjeta con `"Gemini 4 Argon · Semana"` + `"100% · se reinicia en 6d 23h"` y una actualización en caliente cerca del borde inferior confirmando `card.Top + card.ActualHeight <= workArea.Bottom`.

### 5. `PetCoordinator` no limpia `_claudeLimits` ante `NeedsLogin` / `NoCredentials`, dejando el anillo naranja visible hasta 15 min
- Gravedad: Importante
- Archivo: `src/DragonPet/Services/PetCoordinator.cs:166-170`
- Escenario: Claude obtuvo datos hace 2 min (`_claudeLimits` vigente) y el usuario revoca la sesión o borra `.credentials.json` → `FetchAsync` devuelve `ClaudeDataStatus.NeedsLogin` (o `NoCredentials`) con `Limits = null` → la línea 167 solo actualiza `_claudeLimits` si `result.Limits is not null`, manteniendo el dato viejo en memoria durante 13 min más.
- Por qué importa: Incumple el spec («Si el refresco falla (401/400): estado 'sin login', anillo gris, aviso en la tarjeta»): el anillo naranja sigue mostrando el porcentaje anterior y la tarjeta mezcla filas viejas con el aviso de «Inicia sesión».
- Arreglo sugerido: Añadir `else if (result.Status is ClaudeDataStatus.NeedsLogin or ClaudeDataStatus.NoCredentials) { _claudeLimits = null; }`.
- Test que lo demostraría: `Coordinator_NeedsLoginAfterOk_ClearsClaudeRing`: tras un `Ok` con 38 %, un `NeedsLogin` posterior publica `visual.ClaudeRing == null` y `claudeSection.Rows` vacío.

### 6. `PetWindow` puede cerrarse con `Alt+F4` y crashear al pulsar «Mostrar dragón», y `DragMove()` puede lanzar `InvalidOperationException`
- Gravedad: Importante
- Archivo: `src/DragonPet/Windows/PetWindow.cs:33-34`, `44-57` y `src/DragonPet/App.xaml.cs:125-141`
- Escenario:
  1. `PetWindow` no tiene `WS_EX_NOACTIVATE`, así que al hacer clic o arrastrar toma el foco del teclado (robándolo a la app activa). Si entonces el usuario pulsa `Alt+F4` (o cualquier herramienta envía `WM_CLOSE`), `PetWindow` se cierra definitivamente; como `App.xaml` tiene `ShutdownMode="OnExplicitShutdown"`, la app sigue viva en la bandeja, y al pulsar «Mostrar dragón» `TogglePet()` llama a `_pet.Show()` sobre una ventana ya cerrada → `InvalidOperationException` sin capturar.
  2. En `OnMouseDown`, si el botón izquierdo se suelta antes de que `DragMove()` ejecute (clic muy rápido, lápiz o pantalla táctil), `DragMove()` lanza `InvalidOperationException`.
- Por qué importa: Un `Alt+F4` accidental deja el icono de bandeja roto (crashea la app al reabrir el dragón) y un clic táctil/rápido puede tumbar la mascota.
- Arreglo sugerido: En `PetWindow`, suscribir `Closing` para cancelar el cierre y llamar a `Hide()` mientras la app no esté saliendo (sincronizando `_tray.PetVisible = false`), y envolver `DragMove()` en `try/catch (InvalidOperationException)`.
- Test que lo demostraría: Prueba manual: hacer clic en el dragón, pulsar `Alt+F4`, abrir el menú de la bandeja y pulsar «Mostrar dragón» → la app no crashea y el dragón reaparece.

### 7. `TrayIcon.ShowMenu` llama a `SetForegroundWindow` sobre `_owner` oculto y `Tooltip` no comprueba `_disposed`
- Gravedad: Importante
- Archivo: `src/DragonPet/Windows/TrayIcon.cs:76-88`, `103-106`, `110-116` y `src/DragonPet/App.xaml.cs:213`
- Escenario:
  1. El usuario pulsa «Ocultar dragón» → `_pet.Hide()` oculta la ventana, pero `new WindowInteropHelper(_owner).Handle` sigue siendo distinto de `IntPtr.Zero`. Al volver a hacer clic en el icono de la bandeja, `ShowMenu()` pasa ese `HWND` oculto a `SetForegroundWindow` en lugar de `_source.Handle`, por lo que el `ContextMenu` puede quedarse abierto al hacer clic fuera.
  2. En `App.OnExit`, `_tray.Dispose()` se ejecuta antes de detener `_coordinator`; si había un `OnPublish` ya encolado en el `Dispatcher`, asigna `_tray.Tooltip = ...`, y el setter llama a `Shell_NotifyIcon(NimModify, ref _data)` con un `hWnd` y `hIcon` ya destruidos porque no comprueba `_disposed`.
  3. En la línea 103, si `RegisterWindowMessage("TaskbarCreated")` devolviera `0`, `msg == 0` (`WM_NULL`) reañadiría el icono en cada `WM_NULL`.
- Por qué importa: Con el dragón oculto el menú de la bandeja puede no cerrarse al hacer clic fuera, y al salir puede invocar `Shell_NotifyIcon` sobre handles ya liberados.
- Arreglo sugerido: En `ShowMenu()`, usar `_owner.IsVisible && helper.Handle != IntPtr.Zero ? helper.Handle : _source.Handle` (y al cerrar el menú o tras abrir, seguir el patrón de `SetForegroundWindow` en ventana propia); en `Tooltip`, añadir `if (_disposed) return;`; en `WndProc`, comprobar `_taskbarCreated != 0 && msg == (int)_taskbarCreated`.
- Test que lo demostraría: Prueba manual: pulsar «Ocultar dragón», abrir el menú desde la bandeja y hacer clic en el escritorio → el menú se cierra de inmediato; y en un test unitario de `TrayIcon`, llamar a `Dispose()` y luego asignar `Tooltip = "x"` sin fallo.

### 8. `AntigravityClient` puede cachear un puerto incorrecto que devuelva `200` no-JSON y quedarse devolviendo `null` indefinidamente
- Gravedad: Importante
- Archivo: `src/DragonPet/Services/AntigravityClient.cs:35`, `48`, `54`, `104-112`, `131`
- Escenario: Si `language_server` escucha en varios puertos y uno anterior al servicio Connect responde `200 OK` con cuerpo vacío o HTML, `TryPostAsync` lo acepta (solo comprueba `IsSuccessStatusCode`) y `CallAsync` guarda ese puerto en `_baseUrl`/`_csrf`. A continuación `Safe(() => AntigravityParser.ParseTrajectories(body))` captura `FormatException` y devuelve `null` **sin llamar a `Reset()`**, por lo que las siguientes llamadas siguen apuntando al puerto incorrecto para siempre. Además, la validación TLS (línea 35) depende del campo mutable `_baseUrl?.Contains("127.0.0.1")` en vez de inspeccionar `SslStream.TargetHostName`.
- Por qué importa: Si una actualización de Antigravity añade o reordena puertos en `language_server`, la mascota podría engancharse al puerto equivocado y mostrar siempre «Sin datos» aunque el servidor real esté activo.
- Arreglo sugerido: En `TryPostAsync`, verificar que la respuesta tenga contenido JSON (cabecera `application/json` o inicio `{`) antes de aceptarla, llamar a `Reset()` cuando `Safe` capture `FormatException`, y validar `sender is SslStream { TargetHostName: "127.0.0.1" }` en el callback TLS.
- Test que lo demostraría: `AntigravityClient_WrongPortReturns200Html_SkipsAndFindsJsonPort`: simular puerto 1 respondiendo `200 text/html` y puerto 2 respondiendo `200 application/json`, verificando que `GetQuotaAsync` devuelve la cuota del puerto 2.

### 9. `StartupRegistry.Set` no captura errores de registro al pulsar el menú de bandeja y `PetSettings.StartWithWindows` queda sin usar
- Gravedad: Menor
- Archivo: `src/DragonPet/App.xaml.cs:74`, `79` y `src/DragonPet/Services/StartupRegistry.cs:11-28`
- Escenario: Al marcar/desmarcar «Iniciar con Windows» en un PC donde una directiva de grupo bloquea escritura en `HKCU\...\Run`, `StartupRegistry.Set` lanza `UnauthorizedAccessException` o `SecurityException` sin capturar en el manejador `Click` del menú, cerrando la app. Además, `PetSettings.StartWithWindows` se persiste en `settings.json` pero nunca se lee ni se actualiza (la fuente real es el registro).
- Por qué importa: Un fallo de permisos en el registro no debería tumbar la mascota al tocar una casilla del menú.
- Arreglo sugerido: Envolver `StartupRegistry.Set` en `try/catch` (`UnauthorizedAccessException`, `SecurityException`, `IOException`), revirtiendo `_tray.StartupChecked = !on` si falla, y eliminar el campo muerto `StartWithWindows` de `PetSettings` (o mantenerlo sincronizado).
- Test que lo demostraría: `StartupToggled_RegistryFailure_RevertsCheckWithoutCrash` (inyectando un `Action<bool>` que lanza `UnauthorizedAccessException` y comprobando que la casilla vuelve a su estado anterior).

### 10. `DragonArt` deja 3 `RectangleGeometry` sin congelar y `DragonRenderer.Animate` hace ~20 escrituras DP (con boxing) aunque el accesorio no esté visible
- Gravedad: Menor
- Archivo: `src/DragonPet/Rendering/DragonArt.cs:66`, `72`, `74` y `src/DragonPet/Rendering/DragonRenderer.cs:83-119`; `src/DragonPet/Rendering/DragonView.cs:36`
- Escenario: `LaptopScreen`, `ThinkPill` y `AlertBox` se crean con `new RectangleGeometry(...)` sin `.Freeze()` (a diferencia del resto de geometrías de `DragonArt`). Por otro lado, `DragonRenderer.Animate` actualiza en cada fotograma las ~20 propiedades `DependencyProperty` (manos, 3 puntos, 2 `z z`, alerta, destellos, sudor) incluso en `Idle` o `Sleeping` cuando 15–17 de ellas no están en el árbol visual, boxeando un `double` por llamada (~480 B/fotograma); y `DragonView.Visual` reasigna `_timer.Interval` incluso cuando cambia solo el anillo sin cambiar de pose.
- Por qué importa: Las geometrías sin congelar retienen suscripciones de cambio en WPF y el boxing innecesario genera asignaciones evitables en reposo (presupuesto `< 60 MB` RAM y `< 1 %` CPU).
- Arreglo sugerido: Añadir un helper congelado `R(x, y, w, h, r)` en `DragonArt` para los 3 rectángulos; condicionar cada bloque de `Animate` a `v.Pose`/`v.Palette`/`v.Sweat` (evitando escribir `ScaleY = 1` si ya es `1`); y asignar `_timer.Interval` solo si cambió.
- Test que lo demostraría: `DragonArt_AllGeometriesAreFrozen` (reflexión sobre los campos `static readonly Geometry` de `DragonArt` afirmando `g.IsFrozen == true`).

### 11. P/Invoke defensivo: `GetWindowLongPtrW`/`SetWindowLongPtrW` sin respaldo 32 bits ni `SWP_FRAMECHANGED`, y `ListeningPortsByPid` sin comprobar `size <= 0`
- Gravedad: Menor
- Archivo: `src/DragonPet/Windows/NativeMethods.cs:62-72` y `src/DragonPet/Services/LanguageServerLocator.cs:60-64`
- Escenario:
  1. `NativeMethods.cs` importa únicamente `GetWindowLongPtrW` y `SetWindowLongPtrW` (no existen en `user32.dll` de 32 bits, aunque el objetivo sea `win-x64`) y `AddExStyle` no llama a `SetWindowPos(..., SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED)` tras cambiar `GWL_EXSTYLE`.
  2. En `LanguageServerLocator.ListeningPortsByPid`, si la primera llamada a `GetExtendedTcpTable(IntPtr.Zero, ref size, ...)` fallara dejando `size <= 0`, la línea 64 llamaría a `Marshal.AllocHGlobal(0)` y entraría al bucle de 3 intentos innecesariamente.
- Por qué importa: Riesgo menor de incompatibilidad si se compila/ejecuta en x86 o si Windows cachea los estilos extendidos al aplicar `AddExStyle`.
- Arreglo sugerido: Bifurcar por `IntPtr.Size == 8` a `GetWindowLongW`/`SetWindowLongW` y llamar a `SetWindowPos` con `SWP_FRAMECHANGED` en `AddExStyle`; añadir `if (size <= 0) return map;` antes del bucle en `ListeningPortsByPid`.
- Test que lo demostraría: Compilación/verificación de `AddExStyle` leyendo de vuelta `WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE` tras `SourceInitialized` y comprobación de retorno vacío seguro ante tamaño 0.

---

## Cosas que revisé y están bien

- **Estructuras P/Invoke (`NOTIFYICONDATAW` y `MIB_TCPROW_OWNER_PID`)**: el orden de campos, `CharSet.Unicode`, tamaño y alineación en x64 (`976` y `24` bytes), `TCP_TABLE_OWNER_PID_LISTENER = 3` y la conversión de puerto con `(ushort)IPAddress.NetworkToHostOrder((short)row.LocalPort)` son exactos.
- **Handles de bandeja e icono (`TrayIcon` y `DragonIcon`)**: `CreateIconFromResourceEx` usa los parámetros correctos para PNG 32×32 (`dwVer = 0x00030000`), y `Dispose()` es idempotente y libera `NIM_DELETE`, `RemoveHook`, `_source.Dispose()` y `DestroyIcon(_icon)`; el `HwndSource` top-level invisible recibe `TaskbarCreated` cuando se reinicia Explorer.
- **Limpieza de WMI y memoria nativa (`LanguageServerLocator`)**: `ManagementObjectSearcher`, `ManagementObjectCollection`, cada `ManagementBaseObject` y `Marshal.AllocHGlobal` se liberan siempre en bloques `using`/`finally`.
- **Renderizado retenido y temporizador (`DragonView` y `DragonRenderer`)**: grabar la escena una sola vez por cambio de `PetVisual`, mantener todos los `Push*`/`Pop` equilibrados, parar el `DispatcherTimer` al ocultarse (`IsVisibleChanged`) y ajustar a 20/10/4 fps según pose está muy bien planteado para gastar pocos recursos.
- **Tema, foco y click-through de la tarjeta (`CardWindow`)**: consulta `AppsUseLightTheme` cerrando el `RegistryKey`, congela todos los `Brush` del tema, aplica `ShowActivated = false`, `IsHitTestVisible = false` y `WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT`, y omite reconstrucciones si el `CardModel` es igual al mostrado.
- **Instancia única, visibilidad y persistencia (`App`, `SettingsStore`, `PetWindow`)**: el `Mutex` `Local\DragonPet.SingleInstance` evita segundas instancias, `ShutdownMode="OnExplicitShutdown"` permite ocultar el dragón sin cerrar la app, `SettingsStore` tolera JSON corrupto o bloqueado y `PetWindow.Moved` solo guarda al soltar cuando la posición cambió de verdad.
- **Privacidad en diagnósticos (`Diagnostics` y `--log`)**: `--probe-ag` nunca imprime el token CSRF y `--log` solo vuelca estados y porcentajes, sin credenciales ni contenido de conversaciones.
