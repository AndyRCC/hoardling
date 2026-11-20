# Revisión independiente de `DragonPet.Core`

Revisión de solo lectura de `src/DragonPet.Core/*.cs` y `tests/DragonPet.Core.Tests/*.cs` frente al spec (`docs/superpowers/specs/2026-11-18-dragon-pet-design.md`), el plan (`docs/superpowers/plans/2026-11-18-dragon-pet.md`) y el registro de auditorías (`docs/superpowers/audits/audit-log.md`).

---

## Hallazgos

### 1. Errores transitorios (5xx / 429) al refrescar el token de Claude bloquean el cliente como `NeedsLogin`
- Gravedad: Crítico
- Archivo: `src/DragonPet.Core/ClaudeUsageClient.cs:46-50`, `66-73`, `106-116`
- Escenario: El `accessToken` caduca → `RefreshAsync` hace `POST /v1/oauth/token` y Anthropic responde `502`, `503`, `500` o `429` → `RefreshAsync` devuelve `null` para cualquier estado no-2xx → `FetchAsync` llama a `Block()`, marca `_blocked = true` y devuelve `ClaudeDataStatus.NeedsLogin` → `ClaudePollScheduler.OnResult` activa `_waitingForCredentials = true`.
- Por qué importa: Un corte breve o un `429` del endpoint OAuth durante la renovación automática deja el anillo naranja en gris indefinidamente y pide `/login` al usuario aunque su sesión sea válida, sin volver a intentar hasta que cambie `.credentials.json`. Además, el spec especifica explícitamente `NeedsLogin` solo para `401`/`400`.
- Arreglo sugerido: Hacer que `RefreshAsync` devuelva un resultado discriminado (renovado / `InvalidGrant` solo en `400`/`401` / `RateLimited` en `429` con `Retry-After` / `TransientError` en `5xx` o JSON inválido) y llamar a `Block()` únicamente ante `InvalidGrant`.
- Test que lo demostraría: `Client_Refresh503_ReturnsErrorAndDoesNotBlock`: con token caducado y `503` en `TokenUrl`, el primer `FetchAsync` devuelve `ClaudeDataStatus.Error` y un segundo `FetchAsync` (sin tocar el archivo) vuelve a intentar y devuelve `Ok`.

### 2. Si falla `WriteTokens` tras renovar el token, el nuevo `refresh_token` rotado se descarta y puede cerrar la sesión de Claude Code
- Gravedad: Crítico
- Archivo: `src/DragonPet.Core/ClaudeUsageClient.cs:119-126` y `src/DragonPet.Core/AtomicFile.cs:17-29`
- Escenario: El token caduca → `RefreshAsync` envía `R1` → Anthropic rota el refresh token (invalida `R1` y devuelve `A2`/`R2`) → `store.WriteTokens` llama a `File.Move(temp, path, overwrite: true)` justo cuando Claude Code o Windows Defender tiene `.credentials.json` abierto → lanza `IOException` → `FetchAsync` lo captura (línea 78) y descarta `A2`/`R2` → en el siguiente sondeo vuelve a leer y enviar el `R1` ya invalidado → `400 invalid_grant`.
- Por qué importa: Incumple directamente el anti-objetivo del spec («que cierre la sesión de Claude Code del usuario»): un bloqueo de archivo de unos milisegundos en Windows deja tanto a Claude Code como a la mascota sin sesión válida.
- Arreglo sugerido: Añadir reintentos breves ante `IOException` transitorio en `AtomicFile.WriteAllText` (p. ej. 4 intentos con espera de 50–200 ms) y, si aun así falla guardar tras un refresco exitoso, conservar `renewed` en memoria para reintentar escribirlo antes de volver a leer el `R1` viejo del disco.
- Test que lo demostraría: `Client_WriteTokensFailsOnce_RetainsRenewedTokensWithoutReusingOldRefresh`: si el primer guardado falla con `IOException`, la siguiente llamada usa `A2` (y reintenta persistir `A2`/`R2`) en lugar de reenviar `R1` a `TokenUrl`.

### 3. Carrera de refresco concurrente con Claude Code: `Block()` guarda el timestamp del archivo ya renovado y bloquea la app
- Gravedad: Crítico
- Archivo: `src/DragonPet.Core/ClaudeUsageClient.cs:27-52`, `61-73`, `146-151`
- Escenario: `A1`/`R1` caducan → DragonPet y Claude Code leen `A1`/`R1` a la vez → Claude Code renueva primero y escribe `A2`/`R2` en `.credentials.json` → el `POST /v1/oauth/token` de DragonPet con `R1` falla con `400 invalid_grant` → `FetchAsync` llama a `Block()` sin releer el archivo → `_blockedStamp = store.LastWriteTimeUtc` captura el timestamp del archivo **nuevo** (`A2`/`R2`). Además, en el camino de `401` (líneas 61–66), si `reread` cambió de `RefreshToken` pero su `ExpiresAt` está dentro de `ExpirySkew`, la línea 66 pasa el `creds` viejo a `RefreshAsync`.
- Por qué importa: Cada vez que DragonPet y Claude Code coincidan al renovar un token caducado (algo esperable mientras el usuario usa Claude Code), la mascota quedará atascada en «Inicia sesión» pese a que `.credentials.json` ya contiene credenciales válidas.
- Arreglo sugerido: Antes de llamar a `Block()` tras un `400`/`401` de refresco, releer `store.Read()`; si `AccessToken` o `RefreshToken` cambiaron en disco, usar esas credenciales nuevas (y guardar `stampBeforeRead` antes de `store.Read()` para `_blockedStamp`). En la línea 66, refrescar con `reread ?? creds`.
- Test que lo demostraría: `Client_Expired_ConcurrentRefreshByClaudeCode_RecoversWithoutBlocking`: el handler recibe `POST` con `R1`, escribe `A2`/`R2` en `.credentials.json` y responde `400 invalid_grant`; `FetchAsync` relee el archivo, consulta `UsageUrl` con `Bearer A2` y devuelve `ClaudeDataStatus.Ok`.

### 4. Carrera de orden entre `HookServer` y `ClaudeActivityTracker`: un evento anterior puede sobreescribir a `Stop` o `PermissionRequest`
- Gravedad: Importante
- Archivo: `src/DragonPet.Core/HookServer.cs:66`, `80-94`
- Escenario: Claude Code envía `PreToolUse` → `HookServer.HandleAsync` (en una tarea fire-and-forget) responde `200 OK` en la línea 82 **antes** de invocar `onEvent(body)` en la línea 93 → el hilo se suspende un instante → Claude Code recibe `200 OK`, ejecuta una herramienta rápida y envía `Stop` (o `PermissionRequest`) → la segunda tarea ejecuta `onEvent(Stop)` primero y marca `Finished` → la primera tarea reanuda y ejecuta `onEvent(PreToolUse)` después (incluso con un `now` más reciente), sobreescribiendo el estado de la sesión a `Typing`.
- Por qué importa: El dragón se queda «tecleando» hasta 15 minutos después de que Claude Code ya terminó, o pierde una petición de permiso (`NeedsYou`) tapada por el `PreToolUse` previo.
- Arreglo sugerido: Encolar el cuerpo leído en un `Channel<string>` de un solo consumidor **antes** de escribir `200 OK` (manteniendo la respuesta rápida y no bloqueante) y procesar `onEvent` secuencialmente desde ese consumidor.
- Test que lo demostraría: `SequentialPosts_DeliveredStrictlyInOrder`: enviar 200 pares `PreToolUse` seguido de `Stop` de forma secuencial con el mismo `HttpClient` y afirmar que `ClaudeActivityTracker.GetActivity` nunca queda en `Typing` tras completarse el último `Stop`.

### 5. `AntigravityActivityTracker`: el respaldo por `lastModifiedTime` retrasa ~10 s el fin de cada tarea y la animación «Listo»
- Gravedad: Importante
- Archivo: `src/DragonPet.Core/AntigravityActivityTracker.cs:22-36`
- Escenario: Tras un sondeo base `Idle(T0)`, Antigravity trabaja (`Working`, `LatestModified = T0+3s`) y termina en `T0+5s` (`Idle`, `LatestModified = T0+5s`). En el sondeo de `T0+6s`, la línea 22 actualiza `_recentModified = T0+5s` y la línea 34 evalúa `(T0+6s) - (T0+5s) <= 10s`, sobreescribiendo `AgRunState.Idle` por `AppActivity.Thinking`. El dragón sigue trabajando en los sondeos de `T0+9s`, `T0+12s` y `T0+15s`, y recién pasa a `Idle`/`LastFinishedAt` en `T0+18s`.
- Por qué importa: Incluso cuando Antigravity informa correctamente `RUNNING` y `IDLE`, el dragón siempre tarda ~10 s de más en enterarse de que terminó y salta de alegría («Listo») 10 s tarde. Además, el spec indica que `lastModifiedTime` es respaldo «si no hay un estado útil».
- Arreglo sugerido: No activar el respaldo por `lastModifiedTime` cuando la transición viene de un `AgRunState.Working`/`NeedsYou` real hacia `Idle` (o registrar `_sawExplicitState = true` al ver `Working`/`NeedsYou` y desactivar el respaldo mientras los estados explícitos funcionen).
- Test que lo demostraría: `Baseline_Working_Then_IdleWithNewTimestamp_ImmediatelyIdleAndFinished`: `Update(Idle, T0)` en `T0` → `Update(Working, T0+3s)` en `T0+3s` → `Update(Idle, T0+5s)` en `T0+6s` → afirmar `Activity == AppActivity.Idle` y `LastFinishedAt == T0+6s`.

### 6. `CardModel` y `CardSection` rompen la igualdad por valor de `record`, obligando a actualizar la UI cada segundo
- Gravedad: Importante
- Archivo: `src/DragonPet.Core/CardModel.cs:7-9`, `18`, `37`, `55`
- Escenario: `CardModelBuilder.Build(i, ClaudeDataStatus.Ok, true) == CardModelBuilder.Build(i, ClaudeDataStatus.Ok, true)` con exactamente los mismos `PetInputs` devuelve **`false`**, porque los `record` de C# comparan propiedades `IReadOnlyList<T>` por igualdad de referencia y `Build` crea un `new[]` y `List<CardRow>` nuevos en cada llamada.
- Por qué importa: El plan (Tarea 13) especifica que el bucle de 1 s publica en la UI «solo cuando cambia `PetVisual` o `CardModel`». Al ser siempre distintos, cada segundo en reposo despachará trabajo al `Dispatcher` de WPF y reconstruirá la tarjeta, penalizando el presupuesto de `< 1 %` CPU y `< 60 MB` RAM.
- Arreglo sugerido: Sobreescribir `Equals` y `GetHashCode` en `CardModel` y `CardSection` usando `SequenceEqual` (y hacer lo mismo en `ProviderLimits`/`LimitGroup` si se comparan por valor).
- Test que lo demostraría: `CardModel_EqualInputs_AreValueEqual`: llamar dos veces a `CardModelBuilder.Build` con los mismos argumentos y afirmar `Assert.Equal(first, second)` y `first.GetHashCode() == second.GetHashCode()`.

### 7. `InvalidOperationException` no capturada al indexar nodos JSON que no son `JsonObject` en `ClaudeHooksInstaller` y `AntigravityParser`
- Gravedad: Importante
- Archivo: `src/DragonPet.Core/ClaudeHooksInstaller.cs:153-154` y `src/DragonPet.Core/AntigravityParser.cs:70`
- Escenario:
  1. Si `~/.claude/settings.json` contiene `"hooks": { "Stop": ["malformado"] }` (o `[[]]`), `group?["hooks"]` en `ContainsOurs` lanza `InvalidOperationException` (no `FormatException`), evitando el `catch (FormatException)` de `EnsureHooks` e `IsInstalled` y tumbando el arranque.
  2. Si `GetUserStatus` de Antigravity (endpoint no oficial) devuelve `{"userStatus": []}` o `{"userStatus": {"planStatus": "pro"}}`, `root?["userStatus"]?["planStatus"]?["planInfo"]?["planName"]` lanza `InvalidOperationException`.
- Por qué importa: Un `settings.json` mal estructurado debe devolverse como `InvalidSettings` sin crashear (Review Focus 3), y un cambio de esquema en la API no oficial de Antigravity no debe lanzar una excepción inesperada.
- Arreglo sugerido: En `ContainsOurs`, comprobar `group is JsonObject obj && obj["hooks"] is JsonArray ...`. En `ParsePlanName`, navegar usando `as JsonObject` en cada nivel (o añadir un helper `JsonRead.Object(JsonNode?, string)`).
- Test que lo demostraría: `EnsureHooks_NonObjectHookGroup_ReturnsInvalidSettings` (con `{"hooks":{"Stop":["x"]}}`, devuelve `InvalidSettings` sin modificar el archivo) y `PlanName_NonObjectIntermediate_IsNull` (con `{"userStatus":{"planStatus":"pro"}}`, devuelve `null`).

### 8. `HookServer` devuelve `413`/`411`/`431`/`400` en vez de `200`, corta la conexión con cuerpos > 1 MB y no acepta `Transfer-Encoding: chunked`
- Gravedad: Importante
- Archivo: `src/DragonPet.Core/HookServer.cs:80-82`, `122-137`
- Escenario:
  1. En un `PostToolUse` grande (p. ej. Claude lee un archivo o salida de comando > 1 MB), `HookServer` responde `413 Payload Too Large`, cierra el socket sin vaciar el cuerpo (generando TCP RST en Windows) y no llama a `onEvent`, dejando la sesión atascada en `Typing` hasta 15 min.
  2. Si un cliente HTTP/1.1 envía `Transfer-Encoding: chunked` sin `Content-Length`, `HookServer` responde `411 Length Required` y descarta el evento.
- Por qué importa: El spec (y el plan, Tarea 8) exigen que la mascota «responde siempre `200` vacío y rápido» y nunca produzca errores visibles en Claude Code; un `413`/`411` o un TCP RST en `PostToolUse` puede mostrar errores de hook y desincronizar el estado del dragón.
- Arreglo sugerido: Responder siempre `HTTP/1.1 200 OK` (con `Content-Length: 0`) incluso si el cuerpo supera el límite o faltan cabeceras, añadir decodificación básica de `Transfer-Encoding: chunked` (acotada a `MaxBodyBytes`) y, ante un cuerpo > 1 MB, no devolver `413` (y si es posible, extraer `hook_event_name`/`session_id` de un prefijo o descartar limpiamente tras responder `200`).
- Test que lo demostraría: `TooLarge_StillReturns200WithoutCallingOnEvent` (afirma `HTTP/1.1 200`) y `ChunkedPost_DeliversBodyAndReturns200` (envía un POST `Transfer-Encoding: chunked` y verifica que `onEvent` recibe el JSON).

### 9. `CredentialsStore.Read` convierte bloqueos transitorios en `NoCredentials` y lanza `ArgumentOutOfRangeException` con `expiresAt` / `expires_in` fuera de rango
- Gravedad: Importante
- Archivo: `src/DragonPet.Core/CredentialsStore.cs:20-38` y `src/DragonPet.Core/ClaudeUsageClient.cs:27-31`, `78-82`, `118-123`
- Escenario:
  1. Si `File.ReadAllText(Path)` falla por un bloqueo temporal de lectura (Claude Code o antivirus) o permisos (`IOException`/`UnauthorizedAccessException`), `CredentialsStore.Read()` devuelve `null`, indistinguible de archivo inexistente → `ClaudeUsageClient` devuelve `NoCredentials` → `ClaudePollScheduler` deja de sondear para siempre y la tarjeta pide `/login`.
  2. Si `expiresAt` en `.credentials.json` tiene un valor corrupto o en microsegundos/nanosegundos (p. ej. `1781846308293000`), o el endpoint de refresco devuelve `"expires_in": 1e20` o `"NaN"`, `DateTimeOffset.FromUnixTimeMilliseconds` / `clock().AddSeconds` lanza `ArgumentOutOfRangeException`, que no se captura en `Read()` ni en `FetchAsync()`.
- Por qué importa: Un bloqueo de archivo de unos milisegundos puede dejar al usuario falsamente en «Inicia sesión» permanente, y un timestamp corrupto tira el bucle del coordinador.
- Arreglo sugerido: Devolver `null` en `CredentialsStore.Read` solo para `FileNotFoundException`/`DirectoryNotFoundException` (dejando propagar otros `IOException`/`UnauthorizedAccessException` para que `FetchAsync` los convierta en `Error` transitorio) y validar rango finito en `expiresAt` y `expires_in` antes de construir el `DateTimeOffset`.
- Test que lo demostraría: `Client_CredentialsLocked_ReturnsTransientErrorNotNoCredentials` (archivo abierto con `FileShare.None` → `ClaudeDataStatus.Error`) y `Store_OutOfRangeExpiresAt_ReturnsNullWithoutThrowing`.

### 10. `JsonRead.Number` acepta los strings `"NaN"` e `"Infinity"`, propagando `NaN` a porcentajes, anillos y umbrales
- Gravedad: Importante
- Archivo: `src/DragonPet.Core/JsonRead.cs:46`, `78` y `src/DragonPet.Core/Limits.cs:21`
- Escenario: Si un JSON contiene `"utilization": "NaN"` o `"remainingFraction": "NaN"`, `double.TryParse(s, NumberStyles.Float, ...)` devuelve `true` con `double.NaN` → `Math.Clamp(double.NaN, 0, 100)` devuelve `double.NaN` → en `Limits.MaxUsed`, si el primer bucket es `NaN`, oculta todos los buckets válidos posteriores (`85 > NaN` es `false`) → `PetStateMachine` calcula `Math.Max(NaN, 90) >= 80` como `false` (pierde la gota de sudor) y pasa `NaN` al renderizador WPF.
- Por qué importa: Un campo numérico corrupto en una API no oficial puede romper el dibujo del anillo, ocultar alertas de 80 %/100 % y mostrar `NaN %` en la tarjeta.
- Arreglo sugerido: Exigir `double.IsFinite(d)` en `JsonRead.Number` (y devolver `0` o rechazar no finitos en `ClampPercent`).
- Test que lo demostraría: `Parse_NonFiniteStringUtilization_Ignored`: `{"five_hour":{"utilization":"NaN"}}` no añade un bucket con `NaN` y `MaxUsed(FiveHour)` es `null`.

### 11. `CardModelBuilder`: sección de Claude vacía sin aviso cuando caducan los datos con `status == Ok`, y `UpdatedLine` cuenta datos de Antigravity cerrado
- Gravedad: Importante
- Archivo: `src/DragonPet.Core/CardModel.cs:27-39`, `79-95`
- Escenario:
  1. Tras suspender la PC > 15 min, antes de que termine el siguiente sondeo, `claudeStatus` sigue en `Ok` pero `PetStateMachine.Fresh(i.ClaudeLimits, i.Now)` devuelve `null` → `ClaudeSection` emite `Rows = []` y `Notice = null` (conservando además el `Plan` caducado). Lo mismo ocurre si `ClaudeUsageParser` recibió todos los buckets nulos.
  2. Antigravity se cierra (`AgOpen = false`) 1 min después de su último sondeo mientras Claude está en `NeedsLogin` (`ClaudeLimits = null`) → ninguna sección muestra filas, pero `UpdatedLine` incluye `i.AgLimits` y muestra «Actualizado hace 1 min».
- Por qué importa: El usuario ve una sección «Claude» en blanco sin explicación al despertar la PC, o un «Actualizado hace 1 min» engañoso cuando no hay ningún dato activo en la tarjeta.
- Arreglo sugerido: En `ClaudeSection`, si `rows.Count == 0 && notice is null`, asignar un aviso (p. ej. `"Sin datos recientes"`) y usar `fresh?.PlanName`. En `UpdatedLine`, ignorar `i.AgLimits` cuando `!i.AgOpen` (y considerar solo datos vigentes o formatear horas si la antigüedad supera 60 min).
- Test que lo demostraría: `Card_ClaudeOkButStale_ShowsNoticeInsteadOfBlank` (`Assert.NotNull(claude.Notice)`) y `Card_AgClosedAndNoClaudeData_UpdatedLineSaysNoData` (`Assert.Equal("Sin datos todavía", m.UpdatedLine)`).

### 12. `AtomicFile.WriteAllText`: pérdida de ACLs en `.credentials.json`, temporal fijo y `finally` sin protección
- Gravedad: Menor
- Archivo: `src/DragonPet.Core/AtomicFile.cs:17-29`
- Escenario: Al guardar tokens en `~/.claude/.credentials.json`, `File.Move(temp, path, overwrite: true)` sustituye los permisos NTFS (ACLs) del archivo original por los heredados del archivo temporal `.dragonpet.tmp` y rompe enlaces simbólicos. Además, si falla `WriteAllText` o `Move` por un archivo bloqueado, `File.Delete(temp)` en el `finally` no está en `try/catch` (puede tapar la excepción original o dejar tokens en texto claro si falla el borrado), y no se llama a `Flush(flushToDisk: true)`.
- Por qué importa: Reduce la protección de permisos del archivo de credenciales OAuth y aumenta el riesgo de corrupción (archivo de 0 bytes) ante un apagón repentino.
- Arreglo sugerido: Usar un temporal con nombre único (`$"{path}.{Guid.NewGuid():N}.tmp"`), escribir con `FileStream` + `Flush(flushToDisk: true)`, usar `File.Replace` cuando el destino ya exista para conservar ACLs y proteger `File.Delete(temp)` en el `finally`.
- Test que lo demostraría: `AtomicFile_TargetExists_ReplacesAtomicallyWithoutTempLeak` (y en Windows, comprobar que las ACLs del archivo destino se preservan tras `WriteTokens`).

### 13. `AntigravityActivityTracker` no detecta la primera conversación si `LatestModified` pasa de `null` a una fecha
- Gravedad: Menor
- Archivo: `src/DragonPet.Core/AntigravityActivityTracker.cs:22`
- Escenario: Antigravity arranca sin conversaciones (`LatestModified == null`, `_hasBaseline = true`, `_previousModified = null`) → el usuario abre la primera conversación y llega `(Idle, LatestModified = T0)` → en C#, `T0 > (DateTimeOffset?)null` evalúa a `false` → `_recentModified` permanece `null` y `Activity` queda en `Idle`.
- Por qué importa: El respaldo por `lastModifiedTime` ignora la primera actividad de una instalación o espacio de trabajo vacío.
- Arreglo sugerido: Cambiar la condición a `_hasBaseline && snapshot.LatestModified is { } mod && (_previousModified is null || mod > _previousModified)`.
- Test que lo demostraría: `Fallback_FromNullBaselineToRecentModified_Working`: `Update(Idle, null)` en `T0` → `Update(Idle, T0+2s)` en `T0+3s` → `Assert.Equal(AppActivity.Thinking, t.Activity)`.

### 14. `HookServer.DisposeAsync` puede invocar `onEvent` tras finalizar el cierre y asigna 16 KB por petición
- Gravedad: Menor
- Archivo: `src/DragonPet.Core/HookServer.cs:66`, `105`, `156-166`
- Escenario: `DisposeAsync()` cancela `_cts` y espera `_acceptLoop`, pero no espera a las tareas `HandleAsync` ya lanzadas; una conexión aceptada justo antes del cierre puede ejecutar `onEvent(body)` después de que `await server.DisposeAsync()` haya retornado y el coordinador esté cerrado. Además, `new byte[MaxHeaderBytes]` reserva 16 KB en cada hook pese a que las cabeceras ocupan ~200 B.
- Por qué importa: Riesgo de callbacks post-dispose al cerrar la app y presión innecesaria de GC durante sesiones largas de Claude Code.
- Arreglo sugerido: Al mover `onEvent` a un consumidor único de `Channel<string>` (hallazgo 4), cerrar y esperar ese consumidor en `DisposeAsync`; opcionalmente alquilar el búfer de cabeceras de `ArrayPool<byte>.Shared`.
- Test que lo demostraría: `DisposeAsync_NoOnEventCalledAfterCompletion`: con un `onEvent` lento en curso al llamar `DisposeAsync`, afirmar que el callback no se ejecuta después de que `DisposeAsync` retorna.

### 15. `AntigravityParser.ExtractCsrfToken` falla con argumentos entrecomillados enteros (`"--csrf_token=abc"`) o valor vacío (`""`)
- Gravedad: Menor
- Archivo: `src/DragonPet.Core/AntigravityParser.cs:121`
- Escenario: Si la línea de comandos de WMI llega como `x.exe "--csrf_token=abc-123"` (comillas rodeando todo el flag=valor), `(?<v>\S+)` captura `abc-123"` con la comilla final. Si llega `--csrf_token ""`, devuelve `""` literal en lugar de `null`.
- Por qué importa: Un CSRF token con una comilla extra provocaría `401`/`403` en todas las llamadas locales a Antigravity.
- Arreglo sugerido: Restringir el grupo no entrecomillado a `(?<v>[^\s"]+)` y comprobar `string.IsNullOrEmpty` antes de devolver.
- Test que lo demostraría: `Csrf_WholeArgumentQuoted_StripsTrailingQuote` (`"x.exe \"--csrf_token=abc-123\""` → `"abc-123"` y `"x.exe --csrf_token \"\""` → `null`).

### 16. `PetStateMachine.JustFinished` deja al dragón en `Happy` si el reloj del sistema retrocede
- Gravedad: Menor
- Archivo: `src/DragonPet.Core/PetStateMachine.cs:93-94`
- Escenario: Claude termina en `T0` (`ClaudeFinishedAt = T0`); acto seguido el reloj del sistema se ajusta hacia atrás 5 minutos (NTP o cambio manual) → `now - f` es `-5 min`, que cumple `< HappyFor (2 s)` → `JustFinished` devuelve `true` durante los 5 minutos completos.
- Por qué importa: El dragón se quedaría saltando en estado «Listo» varios minutos en lugar de 2 segundos.
- Arreglo sugerido: Acotar también el extremo inferior con un pequeño margen por si el hilo de hooks marca milisegundos por delante: `var elapsed = now - f; return elapsed >= TimeSpan.FromSeconds(-2) && elapsed < HappyFor;`.
- Test que lo demostraría: `Happy_ClockSteppedBack_NotStuckHappy`: `claudeFinishedAt: T0.AddMinutes(5)` con `now: T0` → `Assert.Equal(PetPose.Idle, v.Pose)`.

### 17. `ClaudeCredentials.ToString()` expone `AccessToken` y `RefreshToken` en claro
- Gravedad: Menor
- Archivo: `src/DragonPet.Core/CredentialsStore.cs:7`
- Escenario: `ClaudeCredentials` es un `record` sin `ToString()` personalizado; cualquier inspección, log de depuración o formateo de error que imprima la instancia serializa ambos tokens OAuth en texto claro.
- Por qué importa: Riesgo evitable de fuga de credenciales si en el futuro se añade diagnóstico o telemetría local.
- Arreglo sugerido: Sobreescribir `public override string ToString()` en `ClaudeCredentials` devolviendo solo `ExpiresAt` y `SubscriptionType` (redactando los tokens).
- Test que lo demostraría: `Credentials_ToString_DoesNotExposeTokens`: afirmar `Assert.DoesNotContain(creds.AccessToken, creds.ToString())` y lo mismo para `RefreshToken`.

### 18. Discrepancia menor con el spec en la URL del hook (`/dragonpet` frente a `/hook`)
- Gravedad: Menor
- Archivo: `src/DragonPet.Core/ClaudeHooksInstaller.cs:15`
- Escenario: El spec (línea 156) define la URL del hook como `http://127.0.0.1:47823/hook`, mientras que el plan y `ClaudeHooksInstaller.HookUrl` usan `http://127.0.0.1:47823/dragonpet` (`HookServer` ignora la ruta, por lo que funcionalmente recibe ambos).
- Por qué importa: Divergencia documental con la autoridad del diseño; usar `/dragonpet` es mejor para identificar inequívocamente los hooks propios al desinstalar.
- Arreglo sugerido: Actualizar la línea 156 del spec a `/dragonpet` para mantener una sola fuente de verdad (o alinear ambos).
- Test que lo demostraría: Ya existe `Events_AreTheSpecList`; basta alinear el texto del spec.

### 19. Huecos de cobertura de tests en ramas ya implementadas
- Gravedad: Menor
- Archivo: `tests/DragonPet.Core.Tests/HookServerTests.cs`, `ClaudeHooksInstallerTests.cs`, `ClaudeActivityTrackerTests.cs`, `ClaudeUsageClientTests.cs`, `CardModelBuilderTests.cs`, `TimeFormatTests.cs`
- Escenario: Varias ramas relevantes de producción no tienen ningún test que las ejercite:
  - `HookServer`: cuerpos > 16 KB (que realmente ejercitan `ReadExactlyAsync` en múltiples lecturas), cabeceras > 16 KB (`431`), peticiones sin `Content-Length` (`411`) y cuerpo truncado (`EndOfStreamException`).
  - `ClaudeHooksInstaller`: desinstalación del handler tipo `command` (`DragonPet --hook`, líneas 162–165), conservación de arrays vacíos preexistentes del usuario y desinstalación cuando un grupo mezcla un hook del usuario con el nuestro.
  - `ClaudeActivityTracker`: notificación heredada sin `notification_type` que detecta `"permission"` en `message` (línea 106), y concurrencia multi-hilo real entre `OnHookEvent` y `GetActivity`.
  - `ClaudeUsageClient`: `Retry-After` en formato HTTP-date (línea 134), y distinción entre timeout interno de `HttpClient` (`Error`) frente a cancelación del `CancellationToken` del llamador (debe propagar `OperationCanceledException`).
  - `CardModelBuilder` y `TimeFormat`: `ResetsAt` en el pasado (`"reiniciando"`, línea 65), `AgOpen = true` con `AgLimits` nulo o caducado (`"Cargando…"` / error), y `TimeFormat.Until` con `DateTimeOffset` en zonas horarias distintas (p. ej. UTC vs `-05:00`).
- Por qué importa: Son rutas de error, compatibilidad o concurrencia donde una regresión pasaría desapercibida en CI.
- Arreglo sugerido: Añadir un test unitario focalizado para cada una de estas ramas.
- Test que lo demostraría: `Post_LargeBodyOver16KB_DeliveredIntact`, `Uninstall_RemovesCommandHookAndPreservesUserEmptyArray`, `Notification_LegacyMessagePermission_NeedsYou`, `Client_RetryAfterHttpDate_Parsed`, `Client_TimeoutReturnsError_CallerCancelThrows`, `Card_PastResetTime_ShowsReiniciando` y `Until_DifferentOffsets_ComparesUtc`.

---

## Cosas que revisé y están bien

- **Prioridad de estados (`PetStateMachine`)**: el orden `Durmiendo > Te necesita > Trabajando > Listo > Reposo`, la fusión bicolor, el descarte de actividad cuando una app está cerrada y la caducidad unificada (`Fresh` a 15 min) coinciden con el spec.
- **Parser de uso de Claude (`ClaudeUsageParser`)**: mapea correctamente `five_hour`, `seven_day` y opcionales Opus/Sonnet, recorta porcentajes a `[0, 100]` y maneja nulos y JSON no objeto.
- **Parser de cuota de Antigravity (`AntigravityParser.ParseQuotaSummary`)**: soporta respuesta envuelta o plana, trata la ausencia de `remainingFraction` (proto3) como 100 % usado y mapea correctamente los nombres de grupo y ventanas.
- **Fechas con 9 decimales (`JsonRead.Time`)**: `TrimFraction` recorta correctamente nanosegundos de protobuf a 7 dígitos tanto con `Z`, con offset `±HH:MM` o sin zona horaria (tratada como UTC con `AssumeUniversal`).
- **Formato de tiempos (`TimeFormat.Until`)**: usa resta directa de `DateTimeOffset` (correcta en UTC) y formatea limpiamente todos los rangos (`ya`, `<1m`, `Xm`, `Xh Ym`, `Xd Yh`).
- **Fusión y limpieza de `settings.json` (`ClaudeHooksInstaller`)**: conserva claves y hooks del usuario, evita escapar acentos/`< > &` con `UnsafeRelaxedJsonEscaping`, es idempotente, hace copia `.dragonpet.bak` una sola vez y no toca el archivo ante JSON con comentarios o sintaxis inválida.
- **Programador de sondeos (`ClaudePollScheduler`)**: cumple exactamente 5 min regulares, 20 s tras `Stop` respetando mínimo 60 s, backoff exponencial en `429` (10/20/30/30 min) sin que `Stop` lo acorte, y pausa en `NeedsLogin`/`NoCredentials` hasta `OnCredentialsChanged`.
- **Preservación de campos en `.credentials.json` (`CredentialsStore.WriteTokens`)**: relee el archivo y solo sustituye `accessToken`, `refreshToken` y `expiresAt`, dejando intactos `mcpOAuth`, `scopes` y `organizationUuid`.
- **Aislamiento de `HookServer`**: escucha solo en `127.0.0.1` con `ExclusiveAddressUse = true`, aplica timeout de lectura de 2 s por conexión y aísla excepciones del consumidor `onEvent`.
- **Liberación de handles (`ProcessCheck`)**: llama a `Dispose()` en cada instancia de `Process` devuelta por `Process.GetProcessesByName`, evitando fugas de handles en el sondeo periódico.
