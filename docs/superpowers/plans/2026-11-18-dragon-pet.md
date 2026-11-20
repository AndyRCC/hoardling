# Dragón mascota: plan de implementación

> Nota (2026-11-20): el proyecto se llama ahora **Hoardling**. Este documento conserva el nombre
> original (DragonPet), sus rutas (`src/DragonPet…`) y su URL de hooks (`/dragonpet`) tal como eran
> al escribirlo.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Mascota de escritorio WPF (dragón chibi) que muestra los límites de 5 h de Claude y Antigravity en anillos, el detalle al pasar el mouse, y anima la actividad de Claude Code y Antigravity.

**Architecture:** `DragonPet.Core` (net10.0) contiene toda la lógica comprobable: parsers, trackers de actividad, máquina de estados, instalador de hooks, credenciales, cliente de uso de Claude, servidor de hooks TCP y programación de sondeos. `DragonPet` (net10.0-windows, WPF) contiene lo específico de Windows: descubrimiento del servidor de Antigravity, dibujo del dragón con `DrawingContext`, ventanas, bandeja por P/Invoke y el coordinador que une todo.

**Tech Stack:** .NET 10 SDK, WPF, xUnit, `System.Management` (WMI). Ninguna otra dependencia.

**Spec:** `docs/superpowers/specs/2026-11-18-dragon-pet-design.md`

## Global Constraints

- Windows 11; app `net10.0-windows` con `UseWPF`; Core y tests `net10.0`. Sin WinForms.
- `<Nullable>enable</Nullable>` y `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` en todos los proyectos.
- Interfaz en español. Paletas exactas del spec (tabla "Paletas").
- Puerto de hooks: `47823`. URL del hook: `http://127.0.0.1:47823/dragonpet`.
- Claude usage: `GET https://api.anthropic.com/api/oauth/usage`, cabecera `anthropic-beta: oauth-2025-04-20`.
- Claude refresh: `POST https://platform.claude.com/v1/oauth/token`, `client_id` `9d1c250a-e61b-44d9-88ed-5944d1962f5e`.
- Antigravity: `POST /exa.language_server_pb.LanguageServerService/{RetrieveUserQuotaSummary|GetUserStatus|GetAllCascadeTrajectories}`, cuerpo `{}`, cabeceras `Connect-Protocol-Version: 1`, `X-Codeium-Csrf-Token`.
- Frecuencias: procesos 5 s; Antigravity actividad 3 s, cuota 60 s; Claude 5 min, +20 s tras `Stop`, mínimo 60 s entre llamadas, 429 dobla el intervalo (tope 30 min).
- Animación: 20 fps trabajando/te necesita/listo, 10 fps reposo, 4 fps durmiendo.
- Umbrales: sudor ≥ 80 %; agotado ≥ 99.5 % en cualquier bucket (5 h o semanal); sesión de Claude sin eventos 15 min = parada; "listo" dura 2 s; datos de límites con más de 15 min = sin dato.
- Ajustes: `%APPDATA%\DragonPet\settings.json`. Backup de hooks: `settings.json.dragonpet.bak`.
- Presupuesto: < 60 MB de RAM y < 1 % de CPU en reposo.
- Nada de commits sin que el usuario lo pida: al final de cada tarea se hace `git add -A` (staging) para que la auditoría de la siguiente vea solo su diff.

## Review Focus

1. Bucket de Antigravity sin `remainingFraction` (proto3 omite el 0): debe contar como 100 % usado, no 0 %. Test en Tarea 3.
2. Límites viejos (PC suspendida, sin red, más de 15 min): anillo gris, no el valor antiguo. Test en Tarea 5.
3. `settings.json` del usuario con otros hooks o JSON inválido: nunca perder su contenido; si es inválido, no escribir. Test en Tarea 6.
4. Sesión de Claude Code que muere sin `Stop`, o app de Claude cerrada con una sesión "trabajando": el dragón no puede quedarse trabajando para siempre. Tests en Tareas 4 y 5.
5. Posición guardada fuera de pantalla (monitor desconectado): volver a la esquina inferior derecha del área de trabajo principal. Test en Tarea 12.

## Auditoría de calidad por tarea (pedida por el usuario)

Cada tarea termina con este paso, y sus hallazgos se anotan en `docs/superpowers/audits/audit-log.md`:

1. `dotnet build DragonPet.sln -c Debug` sin warnings y `dotnet test` en verde.
2. Releer el diff de la tarea (`git diff` contra el índice) contra la sección del spec que implementa.
3. Lista: casos borde y entradas inválidas; recursos (objetos `IDisposable`, timers, handles de `Process`, asignaciones por fotograma); hilos (UI solo desde el Dispatcher); nombres e interfaces coherentes con este plan; nada de código muerto.
4. Arreglar lo encontrado antes de pasar a la siguiente tarea y anotar qué se arregló.
5. `git add -A`.

## Estructura de archivos

```
DragonPet.sln
src/DragonPet.Core/
  Limits.cs                 LimitWindow, LimitBucket, LimitGroup, ProviderLimits
  TimeFormat.cs             textos "2h 10m"
  ClaudeUsageParser.cs
  AntigravityParser.cs      cuota, plan, trayectorias, csrf
  ClaudeActivityTracker.cs  AppActivity, HookEventResult
  AntigravityActivityTracker.cs
  PetStateMachine.cs        PetInputs, PetVisual, PetPalette, PetPose
  CardModel.cs              CardModel, CardSection, CardRow, CardModelBuilder
  ClaudeHooksInstaller.cs   JSON puro + ClaudeSettingsFile (IO)
  CredentialsStore.cs
  ClaudeUsageClient.cs
  ClaudePollScheduler.cs
  HookServer.cs
  ProcessCheck.cs
  WindowPlacement.cs
src/DragonPet/
  App.xaml(.cs)             arranque, instancia única, --render-states
  Services/LanguageServerLocator.cs, AntigravityClient.cs, PetCoordinator.cs,
           SettingsStore.cs, StartupRegistry.cs
  Rendering/DragonArt.cs, DragonRenderer.cs, DragonView.cs
  Windows/PetWindow.xaml(.cs), CardWindow.xaml(.cs), NativeMethods.cs, TrayIcon.cs
tests/DragonPet.Core.Tests/  un archivo de tests por clase de Core + Samples/
```

---

### Task 1: Entorno y esqueleto

**Files:**
- Create: `DragonPet.sln`, `src/DragonPet.Core/DragonPet.Core.csproj`, `src/DragonPet/DragonPet.csproj`, `tests/DragonPet.Core.Tests/DragonPet.Core.Tests.csproj`, `Directory.Build.props`, `.gitignore`

- [ ] **Step 1:** Instalar el SDK: `winget install --id Microsoft.DotNet.SDK.10 --silent --accept-package-agreements --accept-source-agreements`. Verificar: `dotnet --list-sdks` muestra `10.0.x`.
- [ ] **Step 2:** `git init`; `dotnet new sln`, `dotnet new classlib` (Core, net10.0), `dotnet new wpf` (DragonPet, net10.0-windows), `dotnet new xunit` (tests, net10.0); referencias App→Core y Tests→Core; `dotnet add src/DragonPet package System.Management`.
- [ ] **Step 3:** `Directory.Build.props` con `Nullable`, `TreatWarningsAsErrors`, `ImplicitUsings`, `LangVersion latest`. En la app: `ServerGarbageCollection=false`, `ConcurrentGarbageCollection=false`, `InvariantGlobalization=false`, `SatelliteResourceLanguages=es`.
- [ ] **Step 4:** Verificar: `dotnet build DragonPet.sln` OK y `dotnet test` OK (test de plantilla).
- [ ] **Step 5:** Auditoría y `git add -A`.

### Task 2: Modelos de límites, formato de tiempo y parser de Claude

**Files:**
- Create: `src/DragonPet.Core/Limits.cs`, `TimeFormat.cs`, `ClaudeUsageParser.cs`
- Test: `tests/DragonPet.Core.Tests/ClaudeUsageParserTests.cs`, `TimeFormatTests.cs`

**Interfaces:**
- Produces:
  - `enum LimitWindow { FiveHour, Weekly, Other }`
  - `record LimitBucket(string Label, LimitWindow Window, double UsedPercent, DateTimeOffset? ResetsAt)`
  - `record LimitGroup(string Name, IReadOnlyList<LimitBucket> Buckets)`
  - `record ProviderLimits(string? PlanName, IReadOnlyList<LimitGroup> Groups, DateTimeOffset FetchedAt)` con `double? MaxUsed(LimitWindow w)` y `bool AnyExhausted` (algún bucket ≥ 99.5)
  - `static string TimeFormat.Until(DateTimeOffset target, DateTimeOffset now)`
  - `static ProviderLimits ClaudeUsageParser.Parse(string json, string? planName, DateTimeOffset now)`; lanza `FormatException` si el JSON no es un objeto
  - `static string? ClaudeUsageParser.PlanFromSubscription(string? subscriptionType)`

- [ ] **Step 1: Tests**
  - `Parse_ReadsFiveHourAndWeek`: `{"five_hour":{"utilization":38.0,"resets_at":"2026-11-19T04:00:00Z"},"seven_day":{"utilization":21,"resets_at":null}}` → un grupo `"Claude"`, buckets `("Sesión 5 h", FiveHour, 38)` y `("Semana", Weekly, 21)`; `MaxUsed(FiveHour) == 38`.
  - `Parse_AddsModelWeeksWhenPresent`: `seven_day_opus` y `seven_day_sonnet` no nulos → labels `"Semana · Opus"` y `"Semana · Sonnet"`; nulos → no aparecen.
  - `Parse_ClampsUtilization`: 130 → 100; −5 → 0.
  - `Parse_AllNull_GivesEmptyBuckets`: `MaxUsed(FiveHour) == null`.
  - `Parse_Garbage_Throws`: `"[]"` → `FormatException`.
  - `AnyExhausted_TrueAt99_5`.
  - `PlanFromSubscription`: `"pro"`→`"Pro"`, `"max"`→`"Max"`, `null`→`null`.
  - `TimeFormat.Until`: 2h10m → `"2h 10m"`; 45 min → `"45m"`; 3 d 4 h → `"3d 4h"`; 20 s → `"<1m"`; pasado → `"ya"`.
- [ ] **Step 2:** Ejecutar y ver fallar: `dotnet test --filter "FullyQualifiedName~ClaudeUsageParserTests|FullyQualifiedName~TimeFormatTests"`.
- [ ] **Step 3:** Implementar con `System.Text.Json.Nodes`.
- [ ] **Step 4:** Ejecutar y ver pasar.
- [ ] **Step 5:** Auditoría y `git add -A`.

### Task 3: Parsers de Antigravity

**Files:**
- Create: `src/DragonPet.Core/AntigravityParser.cs`
- Test: `tests/DragonPet.Core.Tests/AntigravityParserTests.cs`, `tests/DragonPet.Core.Tests/Samples/ag_quota_summary.json` (copia real sin datos personales)

**Interfaces:**
- Consumes: `Limits.cs` (Tarea 2)
- Produces:
  - `static ProviderLimits AntigravityParser.ParseQuotaSummary(string json, string? planName, DateTimeOffset now)`
  - `static string? AntigravityParser.ParsePlanName(string userStatusJson)` (lee `userStatus.planStatus.planInfo.planName`)
  - `enum AgRunState { Idle, Working, NeedsYou }`
  - `record AgTrajectorySnapshot(AgRunState State, DateTimeOffset? LatestModified, IReadOnlyList<string> RawStatuses)`
  - `static AgTrajectorySnapshot AntigravityParser.ParseTrajectories(string json)`
  - `static string? AntigravityParser.ExtractCsrfToken(string commandLine)`

- [ ] **Step 1: Tests**
  - `QuotaSummary_RealSample`: con la muestra → grupos `"Gemini"`, `"Claude y GPT"`, `"Gemini 4 Argon"`; el bucket 5 h de Gemini tiene `UsedPercent` ≈ 5.5 (±0.01); `MaxUsed(FiveHour)` ≈ 5.5.
  - `QuotaSummary_MissingRemainingFraction_Is100Used` (Review Focus 1): bucket sin `remainingFraction` → `UsedPercent == 100`.
  - `QuotaSummary_WindowLabels`: `"5h"` → `("5 h", FiveHour)`; `"weekly"` → `("Semana", Weekly)`; otro → `Other` con su `displayName`.
  - `QuotaSummary_UnknownGroupKeepsDisplayName`.
  - `PlanName_Read` y `PlanName_Missing_IsNull`.
  - `Trajectories_AllIdle` → `Idle`; `LatestModified` = el mayor `lastModifiedTime`.
  - `Trajectories_AnyRunning` (`"CASCADE_RUN_STATUS_RUNNING"`) → `Working`.
  - `Trajectories_WaitingForUser` (status que contiene `"WAIT"`) → `NeedsYou`. Provisional: se confirma en vivo en la Tarea 14.
  - `Trajectories_Empty` (`{}`) → `Idle`, `LatestModified == null`.
  - `Csrf_Extracted`: `"x.exe --csrf_token abc-123 --foo 1"` → `"abc-123"`; también con `=`; ausente → `null`.
- [ ] **Step 2:** Ver fallar. **Step 3:** Implementar. Mapeo de nombres de grupo: `"Gemini Models"`→`"Gemini"`, `"Claude and GPT models"`→`"Claude y GPT"`; el resto, igual.
- [ ] **Step 4:** Ver pasar. **Step 5:** Auditoría y `git add -A`.

### Task 4: Trackers de actividad

**Files:**
- Create: `src/DragonPet.Core/ClaudeActivityTracker.cs`, `src/DragonPet.Core/AntigravityActivityTracker.cs`
- Test: `tests/DragonPet.Core.Tests/ClaudeActivityTrackerTests.cs`, `AntigravityActivityTrackerTests.cs`

**Interfaces:**
- Consumes: `AgTrajectorySnapshot`, `AgRunState` (Tarea 3)
- Produces:
  - `enum AppActivity { Idle, Thinking, Typing, NeedsYou }`
  - `enum HookEventResult { Ignored, Activity, Stop }`
  - `class ClaudeActivityTracker` con `HookEventResult OnHookEvent(string json, DateTimeOffset now)`, `AppActivity GetActivity(DateTimeOffset now)` y `DateTimeOffset? LastFinishedAt`
  - `class AntigravityActivityTracker` con `void Update(AgTrajectorySnapshot s, DateTimeOffset now)`, `void MarkUnavailable(DateTimeOffset now)`, `AppActivity Activity` (trabajando se reporta como `Thinking`) y `DateTimeOffset? LastFinishedAt`

- [ ] **Step 1: Tests de Claude** (JSON de hook con `hook_event_name`, `session_id`, `tool_name`, `notification_type`)
  - `Prompt_Thinking`; `PreToolUse_Typing`; `PostToolUse_BackToThinking`.
  - `PreToolUse_AskUserQuestion_NeedsYou`; `PermissionRequest_NeedsYou`; `Notification_PermissionPrompt_NeedsYou`; `Notification_IdlePrompt_Ignored` (resultado `Ignored`, actividad sin cambio).
  - `Stop_IdleAndSetsFinished`: devuelve `Stop`; `GetActivity == Idle`; `LastFinishedAt == now`.
  - `SubagentStop_KeepsThinking`; `SessionEnd_RemovesSession`.
  - `TwoSessions_Aggregate`: una en Typing y otra en NeedsYou → `NeedsYou`; Typing + Thinking → `Typing`.
  - `StaleSession_After15Min_Idle` (Review Focus 4): Typing en t0 → en t0+15 min 1 s, `Idle`.
  - `InvalidJson_Ignored`: `"{oops"` → `Ignored`, sin excepción.
- [ ] **Step 2: Tests de Antigravity**
  - `Working_Then_Idle_SetsFinished`.
  - `NeedsYou_Passthrough`.
  - `Fallback_ModifiedRecently_Working`: estado `Idle` pero `LatestModified` avanzó y está a menos de 10 s de `now` → `Thinking`; a más de 10 s → `Idle`.
  - `MarkUnavailable_Idle_NoFinished`: si estaba trabajando y se cierra la app, no hay "listo".
- [ ] **Step 3:** Ver fallar. **Step 4:** Implementar (estado por sesión en un `Dictionary<string, (ClaudeSessionState, DateTimeOffset)>`). **Step 5:** Ver pasar. **Step 6:** Auditoría y `git add -A`.

### Task 5: Máquina de estados y modelo de la tarjeta

**Files:**
- Create: `src/DragonPet.Core/PetStateMachine.cs`, `src/DragonPet.Core/CardModel.cs`
- Test: `tests/DragonPet.Core.Tests/PetStateMachineTests.cs`, `CardModelBuilderTests.cs`

**Interfaces:**
- Consumes: `ProviderLimits` (T2), `AppActivity` (T4), `TimeFormat` (T2)
- Produces:
  - `enum PetPalette { Mint, Sleepy, Claude, Antigravity, Fusion }`
  - `enum PetPose { Idle, Sleeping, Thinking, Typing, NeedsYou, Happy }`
  - `record PetInputs(bool ClaudeOpen, bool AgOpen, AppActivity Claude, AppActivity Ag, DateTimeOffset? ClaudeFinishedAt, DateTimeOffset? AgFinishedAt, ProviderLimits? ClaudeLimits, ProviderLimits? AgLimits, DateTimeOffset Now)`
  - `record PetVisual(PetPalette Palette, PetPose Pose, bool Glasses, bool Sweat, bool Exhausted, double? ClaudeRing, double? AgRing, bool RingsDimmed)`
  - `static PetVisual PetStateMachine.Compute(PetInputs i)`
  - `enum ClaudeDataStatus { Ok, NoCredentials, NeedsLogin, Error, Loading }`
  - `record CardRow(string Label, double? UsedPercent, string? Detail)`, `record CardSection(string Title, string? Plan, IReadOnlyList<CardRow> Rows, string? Notice)`, `record CardModel(IReadOnlyList<CardSection> Sections, string StatusLine, string UpdatedLine)`
  - `static CardModel CardModelBuilder.Build(PetInputs i, ClaudeDataStatus claudeStatus, bool hookServerOk)`

- [ ] **Step 1: Tests de la máquina** (reglas del spec, "Estados y prioridad")
  - `NothingOpen_Sleeping`: `Sleepy`, `Sleeping`, sin lentes, `RingsDimmed`, sin sudor ni agotado aunque los límites estén al 100.
  - `ClaudeTyping_OrangeTypingGlasses`; `ClaudeThinking_OrangeThinking`.
  - `AgWorking_BlueAlternates`: pose `Thinking` en `Now` = 0 s y `Typing` en 5 s (fase `floor(unixSeconds/5) % 2`).
  - `BothWorking_FusionTyping`.
  - `NeedsYouBeatsWorking`: Claude NeedsYou + AG trabajando → `Claude`, `NeedsYou`, sin lentes. Ambos NeedsYou → `Fusion`.
  - `Happy_For2Seconds`: `ClaudeFinishedAt = Now - 1s` → `Claude`, `Happy`; `Now - 3s` → `Mint`, `Idle`.
  - `WorkingBeatsHappy`: AG trabajando + Claude terminó hace 1 s → `Antigravity`.
  - `ClaudeClosed_IgnoresStaleActivity` (Review Focus 4): `ClaudeOpen=false`, `Claude=Typing`, `AgOpen=true` → `Mint`, `Idle`.
  - `Rings`: Claude 5 h 38 → `ClaudeRing 38`; AG con buckets 5 h de 6 y 40 → `AgRing 40`; `AgOpen=false` → `AgRing null`.
  - `StaleLimits_RingNull` (Review Focus 2): `FetchedAt = Now - 16 min` → anillo `null` y no cuenta para sudor ni agotado.
  - `Sweat_At80` y `NoSweat_At79_9`.
  - `Exhausted_WhenWeeklyFull`: semanal de Claude 100, 5 h 10 → `Exhausted`.
- [ ] **Step 2: Tests de la tarjeta**
  - `Card_ClaudeRowsWithReset`: fila `("Sesión 5 h", 38, "se reinicia en 2h 10m")`.
  - `Card_NeedsLoginNotice`: `ClaudeDataStatus.NeedsLogin` → la sección Claude tiene `Notice` = `"Inicia sesión: abre una terminal, escribe claude y luego /login"`.
  - `Card_AgClosed`: `AgOpen=false` → sección Antigravity con `Notice` = `"Antigravity está cerrado"` y sin filas.
  - `Card_StatusLine`: Claude Typing y AG cerrado → `"Claude: trabajando · Antigravity: cerrado"`. Textos: `trabajando`, `te necesita`, `en reposo`, `cerrado`.
  - `Card_UpdatedLine`: `FetchedAt` hace 70 s → `"Actualizado hace 1 min"`; sin datos → `"Sin datos todavía"`.
  - `Card_HookServerDown`: `hookServerOk=false` → `StatusLine` termina en `" · sin actividad de Claude (puerto ocupado)"`.
- [ ] **Step 3:** Ver fallar. **Step 4:** Implementar. **Step 5:** Ver pasar. **Step 6:** Auditoría y `git add -A`.

### Task 6: Instalador de hooks de Claude

**Files:**
- Create: `src/DragonPet.Core/ClaudeHooksInstaller.cs`
- Test: `tests/DragonPet.Core.Tests/ClaudeHooksInstallerTests.cs`

**Interfaces:**
- Produces:
  - `static class ClaudeHooksInstaller` con `HookUrl`, `Events` (`UserPromptSubmit`, `PreToolUse`, `PostToolUse`, `PermissionRequest`, `Notification`, `Stop`, `SubagentStop`, `SessionEnd`), `string Install(string? settingsJson, out bool changed)`, `string Uninstall(string settingsJson, out bool changed)` y `bool IsInstalled(string? settingsJson)`; lanzan `FormatException` si el JSON es inválido
  - `static JsonObject ClaudeHooksInstaller.CreateHandler()` → `{"type":"http","url":HookUrl,"timeout":2}`; único sitio donde se define el handler
  - `enum HookInstallOutcome { Installed, AlreadyInstalled, Removed, NotInstalled, InvalidSettings }`
  - `static class ClaudeSettingsFile` con `HookInstallOutcome EnsureHooks(string path)` y `HookInstallOutcome RemoveHooks(string path)`: hace backup `.dragonpet.bak` solo si no existe y escribe de forma atómica (temporal + `File.Move(overwrite)`)

- [ ] **Step 1: Tests**
  - `Install_EmptyOrNull_AddsAllEvents`: cada evento tiene un grupo `{"hooks":[handler]}`.
  - `Install_PreservesUserContent` (Review Focus 3): `model`, `enabledPlugins` y un hook `command` previo del usuario en `Stop` siguen idénticos, y el nuestro se añade como grupo aparte.
  - `Install_Idempotent`: la segunda vez, `changed == false` y el texto es igual.
  - `Uninstall_RemovesOnlyOurs`: quita nuestros handlers y los grupos o eventos que quedan vacíos; el del usuario se queda.
  - `Install_InvalidJson_Throws` y `EnsureHooks_InvalidFile_DoesNotWrite`: el archivo queda byte a byte igual y el resultado es `InvalidSettings`.
  - `EnsureHooks_CreatesBackupOnce`: el `.bak` tiene el contenido original y no se sobrescribe la segunda vez.
  - `EnsureHooks_MissingFile_Creates`.
- [ ] **Step 2:** Ver fallar. **Step 3:** Implementar con `JsonNode` (escritura con sangría de 2). **Step 4:** Ver pasar. **Step 5:** Auditoría y `git add -A`.

### Task 7: Credenciales, cliente de uso de Claude y programación de sondeos

**Files:**
- Create: `src/DragonPet.Core/CredentialsStore.cs`, `ClaudeUsageClient.cs`, `ClaudePollScheduler.cs`
- Test: `tests/DragonPet.Core.Tests/CredentialsStoreTests.cs`, `ClaudeUsageClientTests.cs` (con `HttpMessageHandler` falso), `ClaudePollSchedulerTests.cs`

**Interfaces:**
- Consumes: `ClaudeUsageParser` (T2), `ClaudeDataStatus` (T5)
- Produces:
  - `record ClaudeCredentials(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt, string? SubscriptionType)`
  - `class CredentialsStore(string path)` con `ClaudeCredentials? Read()`, `void WriteTokens(string access, string refresh, DateTimeOffset expiresAt)` (relee, cambia solo esos 3 campos de `claudeAiOauth`, escribe de forma atómica) y `DateTime? LastWriteTimeUtc`
  - `record ClaudeUsageResult(ClaudeDataStatus Status, ProviderLimits? Limits, TimeSpan? RetryAfter, bool RateLimited)`
  - `class ClaudeUsageClient(HttpClient http, CredentialsStore store, Func<DateTimeOffset> clock)` con `Task<ClaudeUsageResult> FetchAsync(CancellationToken ct)`
  - `class ClaudePollScheduler` con `bool IsDue(DateTimeOffset now)`, `void OnStop(DateTimeOffset now)`, `void OnResult(ClaudeUsageResult r, DateTimeOffset now)` y `void OnCredentialsChanged(DateTimeOffset now)`; el primer `IsDue` es `true`

- [ ] **Step 1: Tests**
  - `Store_ReadsFields`; `Store_MissingFile_Null`; `Store_WriteTokens_PreservesOtherFields`: `mcpOAuth`, `scopes` y `organizationUuid` siguen iguales.
  - `Client_ValidToken_GetsUsage`: la petición lleva `Authorization: Bearer A` y `anthropic-beta: oauth-2025-04-20`; el resultado es `Ok` con 5 h = 38.
  - `Client_Expired_RefreshesThenFetches`: hace POST al token URL con `grant_type=refresh_token` y el `client_id`; el archivo queda con el token nuevo y `expiresAt = now + expires_in`.
  - `Client_RefreshFails_NeedsLogin_AndDoesNotRetryUntilFileChanges`: la segunda llamada no hace ninguna petición HTTP hasta que cambia `LastWriteTimeUtc`.
  - `Client_Usage401_RefreshOnceThenNeedsLogin`.
  - `Client_429_RateLimited` y `Client_NoFile_NoCredentials`.
  - `Client_RereadsFileBeforeRefresh`: si otro proceso ya renovó el archivo (`expiresAt` futuro), no refresca.
  - `Scheduler_RegularEvery5Min`; `Scheduler_AfterStop20s_ButMinGap60s`; `Scheduler_429_Doubles_CapsAt30Min` (10, 20, 30, 30); `Scheduler_NeedsLogin_WaitsForCredentialsChange`.
- [ ] **Step 2:** Ver fallar. **Step 3:** Implementar. **Step 4:** Ver pasar. **Step 5:** Auditoría y `git add -A`.

### Task 8: Servidor de hooks y comprobación de procesos

**Files:**
- Create: `src/DragonPet.Core/HookServer.cs`, `src/DragonPet.Core/ProcessCheck.cs`
- Test: `tests/DragonPet.Core.Tests/HookServerTests.cs`

**Interfaces:**
- Produces:
  - `sealed class HookServer(int port, Action<string> onEvent) : IAsyncDisposable` con `bool TryStart()` (`false` si el puerto está ocupado). Usa `TcpListener` en `IPAddress.Loopback`; lee cabeceras (máx. 16 KB) y `Content-Length` (máx. 1 MB), con timeout de lectura de 2 s; siempre responde `HTTP/1.1 200 OK` con `Content-Length: 0` y cierra; `onEvent` recibe el cuerpo
  - `static bool ProcessCheck.AnyRunning(params string[] names)`: libera cada `Process`

- [ ] **Step 1: Tests**
  - `Post_DeliversBodyAndReturns200` con `HttpClient` real contra un puerto libre.
  - `Garbage_DoesNotCrash`: bytes aleatorios y conexión cortada; después, un POST válido sigue funcionando.
  - `TooLarge_Rejected`: `Content-Length` de 2 MB → responde y `onEvent` no se llama.
  - `PortBusy_TryStartFalse`.
- [ ] **Step 2:** Ver fallar. **Step 3:** Implementar. **Step 4:** Ver pasar. **Step 5:** Auditoría y `git add -A`.

### Task 9: Cliente de Antigravity (Windows)

**Files:**
- Create: `src/DragonPet/Services/LanguageServerLocator.cs`, `src/DragonPet/Services/AntigravityClient.cs`

**Interfaces:**
- Consumes: `AntigravityParser` (T3)
- Produces:
  - `record LanguageServerEndpoint(int Pid, string CsrfToken, IReadOnlyList<int> Ports)`
  - `static LanguageServerEndpoint? LanguageServerLocator.Find()`: WMI `Win32_Process` con `Name LIKE 'language_server%'` y puertos en escucha por PID con `GetExtendedTcpTable` (P/Invoke en `iphlpapi.dll`)
  - `sealed class AntigravityClient : IDisposable` con `Task<ProviderLimits?> GetQuotaAsync(CancellationToken)`, `Task<AgTrajectorySnapshot?> GetActivityAsync(CancellationToken)`, `Task<string?> GetPlanNameAsync(CancellationToken)` y `void Reset()`. Cachea el endpoint y el puerto que funcionó; si falla una llamada, vuelve a descubrir una vez. Acepta el certificado autofirmado solo si el host es `127.0.0.1`. Timeout de 5 s.

- [ ] **Step 1:** Implementar. **Step 2:** Verificar en vivo con un modo temporal `DragonPet.exe --probe-ag` que imprime los grupos y `RawStatuses`; esperado: los mismos grupos que en la Tarea 3. **Step 3:** Auditoría (sin secretos en la salida) y `git add -A`.

### Task 10: Dibujo del dragón

**Files:**
- Create: `src/DragonPet/Rendering/DragonArt.cs`, `DragonRenderer.cs`, `DragonView.cs`; modo `--render-states <dir>` en `App.xaml.cs`

**Interfaces:**
- Consumes: `PetVisual`, `PetPalette`, `PetPose` (T5)
- Produces:
  - `static void DragonRenderer.Draw(DrawingContext dc, PetVisual v, double seconds)` en un lienzo lógico de 170 × 170 (dragón de 120 centrado y anillos de radio 78 y 70, grosor 6)
  - `sealed class DragonView : FrameworkElement` con `PetVisual Visual { get; set; }` y un `DispatcherTimer` único (20/10/4 fps según la pose); se detiene cuando la vista no es visible
  - `static int DragonView.FpsFor(PetPose p)`

- [ ] **Step 1:** `DragonArt`: geometrías del chibi B (las mismas coordenadas que el boceto HTML) en `StreamGeometry` congeladas y brushes de las paletas del spec, también congelados. Accesorios: lentes, laptop, burbuja "…", burbuja "!", "z z", gota de sudor, ojos agotados y destellos.
- [ ] **Step 2:** `DragonRenderer.Draw`: respiración, aleteo, parpadeo, tecleo, z flotantes; fusión con `PushClip` a la mitad derecha. Sin asignar memoria por fotograma: transforms reutilizables y nada de `new Pen`/`Brush` dentro de `Draw`.
- [ ] **Step 3:** Exportar PNG: `--render-states out/` escribe un PNG por cada combinación representativa (los 5 estados, la fusión, sudor y agotado).
- [ ] **Step 4:** Verificación visual: abrir los PNG y compararlos con los bocetos aprobados.
- [ ] **Step 5:** Auditoría (sin asignaciones por fotograma, timer detenido cuando no se ve) y `git add -A`.

### Task 11: Bandeja del sistema, ajustes e inicio con Windows

**Files:**
- Create: `src/DragonPet/Windows/NativeMethods.cs`, `TrayIcon.cs`, `src/DragonPet/Services/SettingsStore.cs`, `StartupRegistry.cs`

**Interfaces:**
- Produces:
  - `sealed class TrayIcon : IDisposable` con `TrayIcon(ImageSource icon, string tooltip)` y los eventos `ToggleRequested`, `StartupToggled(bool)`, `RemoveHooksRequested`, `ExitRequested`; `bool StartupChecked { set; }`. Usa `Shell_NotifyIconW` con una ventana de mensajes `HwndSource`; clic derecho → `ContextMenu` de WPF (con `SetForegroundWindow` antes); el icono se crea desde PNG con `CreateIconFromResourceEx`; vuelve a registrarse con el mensaje `TaskbarCreated`.
  - Textos del menú: `Ocultar dragón` / `Mostrar dragón`, `Iniciar con Windows`, `Quitar hooks de Claude`, `Salir`.
  - `record PetSettings(double? Left, double? Top, bool StartWithWindows, bool HooksDisabled)`; `class SettingsStore` con `PetSettings Load()` y `void Save(PetSettings s)` (JSON atómico; si está corrupto, valores por defecto).
  - `static class StartupRegistry` con `bool IsEnabled()` y `void Set(bool enabled, string exePath)`: valor `DragonPet` en `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, ruta entre comillas.

- [ ] **Step 1:** Implementar. **Step 2:** Verificar a mano: el icono aparece, el menú abre y cierra al hacer clic fuera, Salir libera el icono, y al reiniciar el Explorador el icono vuelve. **Step 3:** Auditoría (liberar `HICON` con `DestroyIcon`, `NIM_DELETE` al salir) y `git add -A`.

### Task 12: Ventanas de la mascota y de la tarjeta

**Files:**
- Create: `src/DragonPet.Core/WindowPlacement.cs`, `src/DragonPet/Windows/PetWindow.xaml(.cs)`, `CardWindow.xaml(.cs)`
- Test: `tests/DragonPet.Core.Tests/WindowPlacementTests.cs`

**Interfaces:**
- Consumes: `DragonView` (T10), `CardModel` (T5), `SettingsStore` (T11)
- Produces:
  - `record struct RectD(double X, double Y, double W, double H)`
  - `static (double X, double Y) WindowPlacement.Resolve(double? left, double? top, double w, double h, IReadOnlyList<RectD> workAreas, RectD primary)`
  - `static (double X, double Y) WindowPlacement.CardPosition(RectD pet, double cardW, double cardH, RectD workArea)`: a la izquierda si cabe; si no, a la derecha. Alineada al borde superior del dragón y recortada al área de trabajo.
  - `PetWindow` con `void Apply(PetVisual v)` y el evento `Moved(double left, double top)`
  - `CardWindow` con `void ShowFor(RectD petRect, CardModel model)` y `void HideCard()`

- [ ] **Step 1: Tests**
  - `Resolve_NoSaved_BottomRightOfPrimary`: margen de 12 px desde la esquina inferior derecha del área de trabajo.
  - `Resolve_SavedOnScreen_Kept`.
  - `Resolve_SavedOffScreen_Reset` (Review Focus 5).
  - `Resolve_PartiallyOff_Clamped`: si queda visible menos de la mitad del dragón, se mete dentro.
  - `Card_LeftIfFits_ElseRight`; `Card_ClampedVertically`.
- [ ] **Step 2:** Ver fallar. **Step 3:** Implementar `WindowPlacement`. **Step 4:** Ver pasar.
- [ ] **Step 5:** Ventanas: `WindowStyle=None`, `AllowsTransparency`, fondo transparente, `Topmost`, `ShowInTaskbar=False`, `ShowActivated=False`, y `WS_EX_TOOLWINDOW` en `SourceInitialized`. La tarjeta lleva además `WS_EX_TRANSPARENT | WS_EX_NOACTIVATE` y tema claro u oscuro según `AppsUseLightTheme`. Arrastre con `DragMove` y `Moved` al soltar. Al entrar el mouse se muestra la tarjeta; al salir se oculta. Mientras la tarjeta se ve, se actualiza con cada modelo nuevo.
- [ ] **Step 6:** Auditoría y `git add -A`.

### Task 13: Coordinador y arranque de la app

**Files:**
- Create: `src/DragonPet/Services/PetCoordinator.cs`; Modify: `src/DragonPet/App.xaml(.cs)` (quitar `StartupUri`)

**Interfaces:**
- Consumes: todo lo anterior
- Produces:
  - `sealed class PetCoordinator : IAsyncDisposable` con `PetCoordinator(Dispatcher ui, Action<PetVisual, CardModel> publish, bool hookServerOk)`, `void OnHookEvent(string json)` (seguro entre hilos) y `Task RunAsync(CancellationToken ct)`. Un `PeriodicTimer` de 1 s en segundo plano aplica las frecuencias de las restricciones globales y publica en la UI solo cuando cambia `PetVisual` o `CardModel`. Los fallos de red nunca rompen el bucle.
  - Arranque en `App.OnStartup`: mutex `Local\DragonPet.SingleInstance` (si ya existe, salir); `ClaudeSettingsFile.EnsureHooks(%USERPROFILE%\.claude\settings.json)`; `HookServer.TryStart()`; crear las ventanas, la bandeja y el coordinador. El resultado de los hooks y del puerto se ve en la tarjeta.
  - "Quitar hooks de Claude" → `ClaudeSettingsFile.RemoveHooks`, con un aviso en la tarjeta. Las siguientes veces que arranque no se reinstalan: `PetSettings.HooksDisabled = true`.

- [ ] **Step 1:** Implementar (si `PetSettings.HooksDisabled`, no llamar a `EnsureHooks`).
- [ ] **Step 3:** Verificar: la app arranca, el dragón se ve abajo a la derecha y la tarjeta aparece al pasar el mouse con datos reales de Antigravity. Una segunda instancia sale sola.
- [ ] **Step 4:** Auditoría (hilos, cancelación al salir, excepciones no observadas) y `git add -A`.

### Task 14: Verificación en vivo, recursos y publicación

**Files:**
- Modify: según lo que salga; Create: `README.md` (cómo instalar, usar y quitar)

- [ ] **Step 1:** El usuario hace `claude` → `/login`. Verificar el anillo naranja con el dato real y ajustar `ClaudeUsageParser` a la forma real de la respuesta si hace falta (se añade la muestra a los tests).
- [ ] **Step 2:** Claude Code (pestaña Code): mandar un mensaje → piensa / escribe / listo; provocar un permiso → te necesita.
- [ ] **Step 3:** Cerrar la mascota y usar Claude Code: ¿aparecen errores por los hooks HTTP? Si aparecen, cambiar `CreateHandler()` a `{"type":"command","command":"\"<exe>\" --hook","timeout":2}` y añadir el modo `--hook`: lee stdin, hace POST con timeout de 1 s y siempre sale con código 0. Repetir la prueba.
- [ ] **Step 4:** Antigravity (el usuario permitió tomar control): lanzar una tarea, registrar los valores reales de `status` con `--probe-ag`, ajustar `ParseTrajectories` y el test provisional de la Tarea 3, y comprobar azul, fusión y, si existe, te necesita.
- [ ] **Step 5:** Cerrar ambas apps → duerme.
- [ ] **Step 6:** `dotnet publish src/DragonPet -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist`. Medir 5 min en reposo y 5 min trabajando: Working Set < 60 MB y CPU < 1 % en reposo. Si no se cumple, perfilar y corregir.
- [ ] **Step 7:** Quitar `--probe-ag` o dejarlo oculto; README; activar "Iniciar con Windows" si el usuario quiere. Auditoría final de todo el proyecto.
