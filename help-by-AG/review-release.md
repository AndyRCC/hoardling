# Auditoría previa al lanzamiento en GitHub (`DragonPet`)

Revisión integral de solo lectura de todo el repositorio (`src/`, `tests/`, `docs/`, `README.md`, `.gitignore`, archivos en raíz) orientada a publicación pública en GitHub y uso en cualquier PC con Windows 11. Cada hallazgo incluye el parche exacto o bloque de código listo para aplicar.

---

## Hallazgos

### 1. `JobEscape`: inyección de argumentos en `Quote()`, relanzamiento WMI antes del mutex, posible `NullReferenceException` y fallo si se ejecuta con `dotnet.exe`
- Gravedad: Crítico
- Archivo: `src/DragonPet/Services/JobEscape.cs:20-69`, `90` y `src/DragonPet/App.xaml.cs:65-81`
- Escenario:
  1. **Quoting roto para `CommandLineToArgvW`:** `Quote(arg)` solo hace `arg.Replace("\"", "\\\"")` sin duplicar barras invertidas previas ni finales, y no cita cadenas vacías ni tabulaciones. Un argumento como `a\" --flag` produce `"a\\" --flag"`, donde `\\` escapa a `\` literal y la `"` cierra el argumento, inyectando `--flag` como argumento nuevo en `Win32_Process.Create` / `CreateProcessW`. Una ruta con espacio acabada en `\` (`C:\Mi Carpeta\`) escapa la comilla de cierre.
  2. **Reenvío innecesario de argumentos arbitrarios:** tras manejar `--probe-ag`, `--render-*` y `--quit` en `App.OnStartup`, el único flag de ejecución que debe conservarse al relanzar es `--log`. Reenviar todos los `args` sin filtrar amplía innecesariamente la superficie hacia WMI.
  3. **Relanzamiento antes del mutex:** `App.OnStartup` llama a `JobEscape.TryRelaunchOutsideJob` (línea 66) antes de comprobar si `Local\DragonPet.SingleInstance` ya existe (línea 73). Abrir una segunda instancia dispara WMI y crea un proceso nuevo solo para salir al instante.
  4. **Bordes de `TryWmi` y `dotnet.exe`:** `output["ReturnValue"]` (línea 63) lanza `NullReferenceException` si `InvokeMethod` devuelve `null`; si alguien ejecuta `dotnet DragonPet.dll`, `Environment.ProcessPath` es `dotnet.exe`, así que relanzar `"dotnet.exe" --detached` falla en silencio y cierra la app. Además, `CreateProcess(null, ...)` conviene llamarlo con `applicationName: exe`.
- Por qué importa: Riesgo de seguridad (inyección de argumentos al relanzar por WMI), arranque silenciosamente roto al lanzar la `.dll` con `dotnet`, y llamadas innecesarias a WMI al abrir una segunda instancia.
- Arreglo sugerido (parche exacto):

```diff
--- a/src/DragonPet/Services/JobEscape.cs
+++ b/src/DragonPet/Services/JobEscape.cs
@@ -19,18 +19,25 @@ internal static class JobEscape
     /// <summary>How a detached copy was started ("breakaway" or "wmi"), or null to keep running here.</summary>
     public static string? TryRelaunchOutsideJob(string[] args)
     {
         if (args.Contains(DetachedFlag) || Environment.ProcessPath is not { } exe)
         {
             return null;
         }
+        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
+        {
+            return null;
+        }
         if (!IsProcessInJob(GetCurrentProcess(), IntPtr.Zero, out var inJob) || !inJob)
         {
             return null;
         }
-        var commandLine = string.Join(' ', new[] { Quote(exe), DetachedFlag }.Concat(args.Select(Quote)));
+        var forwarded = args.Contains("--log")
+            ? $"{Quote(exe)} {DetachedFlag} --log"
+            : $"{Quote(exe)} {DetachedFlag}";
         var directory = Path.GetDirectoryName(exe);
         // WMI first: the new process lands where Explorer-launched apps live. Breakaway only leaves the
         // innermost job and, measured with Claude, stayed inside an outer job owned by the starter.
-        if (TryWmi(commandLine, directory))
+        if (TryWmi(forwarded, directory))
         {
             return "wmi";
         }
-        return TryBreakaway(commandLine, directory) ? "breakaway" : null;
+        return TryBreakaway(exe, forwarded, directory) ? "breakaway" : null;
     }
 
-    private static bool TryBreakaway(string commandLine, string? directory)
+    private static bool TryBreakaway(string exe, string commandLine, string? directory)
     {
         var startup = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>() };
-        if (!CreateProcess(null, new System.Text.StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false,
+        if (!CreateProcess(exe, new System.Text.StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false,
                 CreateBreakawayFromJob | CreateUnicodeEnvironment, IntPtr.Zero, directory, ref startup, out var info))
         {
             return false; // the job does not allow breakaway
         }
         CloseHandle(info.hThread);
         CloseHandle(info.hProcess);
         return true;
     }
 
     private static bool TryWmi(string commandLine, string? directory)
     {
         try
         {
             using var processClass = new ManagementClass("Win32_Process");
             using var input = processClass.GetMethodParameters("Create");
             input["CommandLine"] = commandLine;
             input["CurrentDirectory"] = directory;
             using var output = processClass.InvokeMethod("Create", input, null);
-            return Convert.ToUInt32(output["ReturnValue"]) == 0;
+            return output?["ReturnValue"] is { } rc && Convert.ToUInt32(rc) == 0;
         }
         catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
         {
             return false;
         }
     }
@@ -87,7 +94,37 @@ internal static class JobEscape
     [DllImport("kernel32.dll", SetLastError = true)]
     private static extern bool QueryInformationJobObject(IntPtr job, int infoClass, byte[] info, int length, out int returned);
 
-    private static string Quote(string arg) => arg.Contains(' ') || arg.Contains('"') ? $"\"{arg.Replace("\"", "\\\"")}\"" : arg;
+    /// <summary>Quotes one argument according to Windows CommandLineToArgvW rules.</summary>
+    internal static string Quote(string arg)
+    {
+        if (arg.Length > 0 && !arg.AsSpan().ContainsAny(' ', '\t', '"'))
+        {
+            return arg;
+        }
+        var sb = new System.Text.StringBuilder(arg.Length + 2).Append('"');
+        var backslashes = 0;
+        foreach (var ch in arg)
+        {
+            if (ch == '\\')
+            {
+                backslashes++;
+            }
+            else if (ch == '"')
+            {
+                sb.Append('\\', backslashes * 2 + 1).Append('"');
+                backslashes = 0;
+            }
+            else
+            {
+                if (backslashes > 0)
+                {
+                    sb.Append('\\', backslashes);
+                    backslashes = 0;
+                }
+                sb.Append(ch);
+            }
+        }
+        if (backslashes > 0)
+        {
+            sb.Append('\\', backslashes * 2);
+        }
+        return sb.Append('"').ToString();
+    }
```

Y en `src/DragonPet/App.xaml.cs` (evitar WMI si ya hay otra instancia):

```diff
--- a/src/DragonPet/App.xaml.cs
+++ b/src/DragonPet/App.xaml.cs
@@ -62,6 +62,13 @@ public partial class App : Application
             return;
         }
 
+        if (Mutex.TryOpenExisting(@"Local\DragonPet.SingleInstance", out var running))
+        {
+            running.Dispose();
+            Shutdown(0);
+            return;
+        }
+
         // Started by a terminal, an IDE or Claude Code? Move out of their job so closing them can't kill the dragon.
         if (JobEscape.TryRelaunchOutsideJob(e.Args) is { } how)
         {
```

- Test que lo demostraría: `Quote_HandlesSpacesQuotesAndTrailingBackslashes`: comprobar `Quote(@"C:\My Folder\") == "\"C:\\My Folder\\\\\""` y `Quote(@"a\"" b") == "\"a\\\\\\\" b\""` (y extraer `Quote` a `Core` si se quiere cubrir directamente desde `DragonPet.Core.Tests`).

---

### 2. Detección de Antigravity frágil en otras PCs: nombre exacto `"language_server"` y ruta fija `"\antigravity\"`, y orden no priorizado en `LanguageServerLocator`
- Gravedad: Crítico
- Archivo: `src/DragonPet/Services/PetCoordinator.cs:185`, `src/DragonPet.Core/ProcessCheck.cs:30-50` y `src/DragonPet/Services/LanguageServerLocator.cs:22-54`
- Escenario:
  1. `PetCoordinator` llama a `ProcessCheck.AnyRunningFrom("language_server", @"\antigravity\")`. `Process.GetProcessesByName("language_server")` solo hace coincidencia **exacta**: si en otra versión o PC el ejecutable es `language_server_windows_x64.exe` (como prevé `LanguageServerLocator` con `Name LIKE 'language_server%'`), devuelve `false` siempre.
  2. Si otro usuario instala Antigravity en una carpeta personalizada que no contiene `\antigravity\` (p. ej. `D:\Apps\AG\`), `AnyRunningFrom` devuelve `false` y el coordinador nunca sondea Antigravity, aunque esté abierto.
  3. Por el contrario, si el usuario tiene abierto Windsurf/Codeium además de Antigravity, `LanguageServerLocator.FindAll()` devuelve todos los `language_server*` en orden arbitrario de WMI y puede quedarse con el servidor equivocado.
- Por qué importa: En otra PC con ruta personalizada o con `language_server_windows_x64.exe`, la mitad de la app (anillo azul y actividad de Antigravity) quedará permanentemente en «Antigravity está cerrado». Comprobar además `Antigravity` con ventana visible (`MainWindowHandle != IntPtr.Zero`) sigue ignorando los *sidecars* sin ventana (que motivaron el cambio) y funciona en cualquier ruta de instalación.
- Arreglo sugerido (parche exacto):

```diff
--- a/src/DragonPet.Core/ProcessCheck.cs
+++ b/src/DragonPet.Core/ProcessCheck.cs
@@ -48,4 +48,49 @@ public static class ProcessCheck
         }
         return found;
     }
+
+    /// <summary>
+    /// True if any running process whose name starts with <paramref name="namePrefix"/> (case-insensitive)
+    /// matches <paramref name="pathFragment"/> (when provided). Releases every handle.
+    /// </summary>
+    public static bool AnyRunningPrefix(string namePrefix, string? pathFragment = null)
+    {
+        var processes = Process.GetProcesses();
+        var found = false;
+        foreach (var p in processes)
+        {
+            try
+            {
+                if (!found && p.ProcessName.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase))
+                {
+                    found = pathFragment is null
+                        || p.MainModule?.FileName.Contains(pathFragment, StringComparison.OrdinalIgnoreCase) == true;
+                }
+            }
+            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
+            {
+            }
+            finally
+            {
+                p.Dispose();
+            }
+        }
+        return found;
+    }
+
+    /// <summary>True if a process with this name has a top-level window (excludes headless sidecars).</summary>
+    public static bool AnyRunningWithWindow(string name)
+    {
+        var processes = Process.GetProcessesByName(name);
+        var found = false;
+        foreach (var p in processes)
+        {
+            try
+            {
+                if (!found && p.MainWindowHandle != IntPtr.Zero)
+                {
+                    found = true;
+                }
+            }
+            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
+            {
+            }
+            finally
+            {
+                p.Dispose();
+            }
+        }
+        return found;
+    }
 }
```

```diff
--- a/src/DragonPet/Services/PetCoordinator.cs
+++ b/src/DragonPet/Services/PetCoordinator.cs
@@ -182,7 +182,8 @@ public sealed class PetCoordinator : IAsyncDisposable
             var claudeOpen = ProcessCheck.AnyRunning("claude");
             // Not "any Antigravity.exe": its background sidecars (Antigravity.exe running main.mjs) outlive
             // the app. Its own language server lives exactly as long as the app and is what we read from.
-            var agNowOpen = ProcessCheck.AnyRunningFrom("language_server", @"\antigravity\");
+            var agNowOpen = ProcessCheck.AnyRunningPrefix("language_server", @"\antigravity\")
+                || ProcessCheck.AnyRunningWithWindow("Antigravity");
             if (agOpen && !agNowOpen)
             {
                 _ag.Reset();
```

```diff
--- a/src/DragonPet/Services/LanguageServerLocator.cs
+++ b/src/DragonPet/Services/LanguageServerLocator.cs
@@ -16,22 +16,23 @@ public static class LanguageServerLocator
     public static IReadOnlyList<LanguageServerEndpoint> FindAll()
     {
         var result = new List<LanguageServerEndpoint>();
-        List<(int Pid, string Csrf)> candidates = [];
+        List<(int Pid, string Csrf, bool IsAntigravity)> candidates = [];
         try
         {
             using var searcher = new ManagementObjectSearcher(
-                "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name LIKE 'language_server%'");
+                "SELECT ProcessId, CommandLine, ExecutablePath FROM Win32_Process WHERE Name LIKE 'language_server%'");
             using var found = searcher.Get();
             foreach (var item in found)
             {
                 using (item)
                 {
                     var pid = Convert.ToInt32(item["ProcessId"]);
                     if (item["CommandLine"] is string cmd && AntigravityParser.ExtractCsrfToken(cmd) is { } csrf)
                     {
-                        candidates.Add((pid, csrf));
+                        var path = item["ExecutablePath"] as string ?? "";
+                        candidates.Add((pid, csrf, path.Contains("antigravity", StringComparison.OrdinalIgnoreCase)));
                     }
                 }
             }
@@ -44,7 +45,8 @@ public static class LanguageServerLocator
             return result;
         }
         var ports = ListeningPortsByPid();
-        foreach (var (pid, csrf) in candidates)
+        // Prefer Antigravity's own language server when another Codeium/Windsurf server also runs.
+        foreach (var (pid, csrf, _) in candidates.OrderByDescending(c => c.IsAntigravity))
         {
             if (ports.TryGetValue(pid, out var list) && list.Count > 0)
             {
@@ -88,7 +90,10 @@ public static class LanguageServerLocator
                     {
                         map[pid] = list = [];
                     }
-                    list.Add(port);
+                    if (!list.Contains(port))
+                    {
+                        list.Add(port);
+                    }
                 }
                 return map;
             }
```

- Test que lo demostraría: `ProcessCheck_AnyRunningPrefix_MatchesCurrentProcessPrefix` (en `HookServerTests.cs`, usando los primeros 4 caracteres de `me.ProcessName` con carpeta válida y con carpeta inexistente).

---

### 3. `HookServer` y `ClaudeActivityTracker`: CSRF desde navegadores (`Origin`), falta límite de conexiones/sesiones, `Essentials` pierde `agent_id` (>1 MB) y `Regex.Unescape` puede lanzar `ArgumentException`
- Gravedad: Importante
- Archivo: `src/DragonPet.Core/HookServer.cs:26-28`, `70-97`, `112-217` y `src/DragonPet.Core/ClaudeActivityTracker.cs:19`, `65`
- Escenario:
  1. **CSRF / DNS rebinding desde el navegador:** cualquier web abierta puede hacer `fetch("http://127.0.0.1:47823/dragonpet", { method: "POST", body: ... })` (sin preflight CORS porque el `Content-Type` es `text/plain`) para falsificar el estado del dragón, disparar `OnStop` o inundar `_sessions`. `HookServer` no rechaza cabeceras `Origin`, no verifica el método/ruta (`POST /dragonpet`) ni comprueba que `Host` sea loopback.
  2. **Sin tope de conexiones simultáneas ni de sesiones en memoria:** un proceso local o web puede abrir cientos de POSTs de 1 MB a la vez (picos de cientos de MB) o enviar millones de `session_id` únicos que permanecen 15 min en `_sessions`.
  3. **Regresión con subagentes > 1 MB:** `HookServer.EssentialField()` (línea 200) solo extrae `hook_event_name|session_id|tool_name|notification_type` y **descarta `agent_id`**. Un `PostToolUse` > 1 MB de un subagente llegaría sin `agent_id` a `ClaudeActivityTracker`, reactivando el bug «¿por qué está pensando?».
  4. **Excepción en `Regex.Unescape`:** la línea 194 usa `Regex.Unescape` (escapado de expresiones regulares, no JSON) sobre un campo JSON. Un escape inválido para regex (p. ej. `\uZZZZ`) lanza `RegexParseException` (subclase de `ArgumentException`), que `HandleAsync` (línea 92) no captura, provocando una excepción no observada y cortando la conexión sin responder `200 OK`.
- Por qué importa: Cierra el vector CSRF desde navegadores, acota el uso de memoria ante tráfico abusivo en loopback, evita que un escape raro aborte la respuesta `200 OK` a Claude Code y mantiene el filtrado de subagentes en cuerpos > 1 MB.
- Arreglo sugerido (parche exacto):

```diff
--- a/src/DragonPet.Core/HookServer.cs
+++ b/src/DragonPet.Core/HookServer.cs
@@ -1,7 +1,9 @@
 using System.Buffers;
+using System.Globalization;
 using System.Net;
 using System.Net.Sockets;
 using System.Text;
+using System.Text.Json;
 using System.Text.Json.Nodes;
 using System.Text.RegularExpressions;
 
@@ -19,12 +21,14 @@ public sealed partial class HookServer(int port, Action<string> onEvent) : IAsyn
     public const int MaxHeaderBytes = 16 * 1024;
     public const int MaxBodyBytes = 1024 * 1024;
     private const int OversizePrefixBytes = 16 * 1024;
+    private const int MaxConcurrentConnections = 16;
     public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(2);
     private static readonly byte[] HeaderEnd = "\r\n\r\n"u8.ToArray();
     private static readonly byte[] OkReply = "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray();
 
     private readonly CancellationTokenSource _cts = new();
     private TcpListener? _listener;
     private Task? _acceptLoop;
+    private int _active;
 
     public bool TryStart()
     {
@@ -67,6 +71,12 @@ public sealed partial class HookServer(int port, Action<string> onEvent) : IAsyn
             {
                 continue;
             }
+            if (Interlocked.Increment(ref _active) > MaxConcurrentConnections)
+            {
+                Interlocked.Decrement(ref _active);
+                client.Dispose();
+                continue;
+            }
             _ = HandleAsync(client, ct);
         }
     }
@@ -75,24 +85,31 @@ public sealed partial class HookServer(int port, Action<string> onEvent) : IAsyn
     {
         // Never run a connection on the accept loop's thread, even when reads complete synchronously.
         await Task.Yield();
-        using (client)
-        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
+        try
         {
-            timeout.CancelAfter(ReadTimeout);
-            try
+            using (client)
+            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
             {
-                var stream = client.GetStream();
-                var body = await ReadRequestAsync(stream, timeout.Token);
-                if (body is not null)
+                timeout.CancelAfter(ReadTimeout);
+                try
                 {
-                    Deliver(body);
+                    var stream = client.GetStream();
+                    var body = await ReadRequestAsync(stream, timeout.Token);
+                    if (body is not null)
+                    {
+                        Deliver(body);
+                    }
+                    await stream.WriteAsync(OkReply, timeout.Token);
                 }
-                await stream.WriteAsync(OkReply, timeout.Token);
-            }
-            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
-            {
-                // Broken or slow client: nothing to do.
+                catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
+                {
+                    // Broken or slow client: nothing to do.
+                }
             }
+        }
+        finally
+        {
+            Interlocked.Decrement(ref _active);
         }
     }
 
@@ -130,8 +147,12 @@ public sealed partial class HookServer(int port, Action<string> onEvent) : IAsyn
                 filled += n;
             }
 
-            var length = ContentLength(Encoding.ASCII.GetString(buffer, 0, headerEnd));
-            if (length is not { } total)
+            var headerText = Encoding.ASCII.GetString(buffer, 0, headerEnd);
+            if (!IsAllowedRequest(headerText, out var total))
             {
                 return null;
             }
@@ -191,29 +212,68 @@ public sealed partial class HookServer(int port, Action<string> onEvent) : IAsyn
             var key = m.Groups["k"].Value;
             if (result[key] is null)
             {
-                result[key] = Regex.Unescape(m.Groups["v"].Value);
+                if (DecodeJsonString(m.Groups["v"].Value) is { } decoded)
+                {
+                    result[key] = decoded;
+                }
             }
         }
         return result["hook_event_name"] is null ? null : result.ToJsonString();
     }
 
-    [GeneratedRegex("\"(?<k>hook_event_name|session_id|tool_name|notification_type)\"\\s*:\\s*\"(?<v>(?:[^\"\\\\]|\\\\.)*)\"")]
+    private static string? DecodeJsonString(string raw)
+    {
+        try
+        {
+            return JsonSerializer.Deserialize<string>("\"" + raw + "\"");
+        }
+        catch (JsonException)
+        {
+            return null;
+        }
+    }
+
+    [GeneratedRegex("\"(?<k>hook_event_name|session_id|tool_name|notification_type|agent_id)\"\\s*:\\s*\"(?<v>(?:[^\"\\\\]|\\\\.)*)\"")]
     private static partial Regex EssentialField();
 
-    private static int? ContentLength(string headers)
+    /// <summary>
+    /// Accepts only direct loopback POSTs to /dragonpet (or /hook) without a browser Origin header,
+    /// preventing cross-origin web pages from spoofing hook events or triggering polls.
+    /// </summary>
+    private static bool IsAllowedRequest(string headers, out int contentLength)
     {
-        foreach (var line in headers.Split("\r\n"))
+        contentLength = -1;
+        var lines = headers.Split("\r\n");
+        if (lines.Length == 0)
         {
+            return false;
+        }
+        var req = lines[0];
+        if (!req.StartsWith("POST /dragonpet ", StringComparison.Ordinal)
+            && !req.StartsWith("POST /hook ", StringComparison.Ordinal))
+        {
+            return false;
+        }
+        for (var i = 1; i < lines.Length; i++)
+        {
+            var line = lines[i];
             var colon = line.IndexOf(':');
-            if (colon > 0
-                && line.AsSpan(0, colon).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
-                && int.TryParse(line.AsSpan(colon + 1).Trim(), out var value)
-                && value >= 0)
+            if (colon <= 0)
             {
-                return value;
+                continue;
+            }
+            var name = line.AsSpan(0, colon).Trim();
+            var val = line.AsSpan(colon + 1).Trim();
+            if (name.Equals("Origin", StringComparison.OrdinalIgnoreCase))
+            {
+                return false;
+            }
+            if (name.Equals("Host", StringComparison.OrdinalIgnoreCase) && !IsLoopbackHost(val))
+            {
+                return false;
+            }
+            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
+                && int.TryParse(val, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
+            {
+                contentLength = parsed;
             }
         }
-        return null;
+        return contentLength >= 0;
     }
+
+    private static bool IsLoopbackHost(ReadOnlySpan<char> host)
+    {
+        var colon = host.IndexOf(':');
+        var name = colon >= 0 ? host[..colon] : host;
+        return name.Equals("127.0.0.1", StringComparison.Ordinal)
+            || name.Equals("localhost", StringComparison.OrdinalIgnoreCase);
+    }
```

*(Nota: en `HookServerTests.cs`, `RawPostReadStatusAsync` y `MissingContentLength_StillReturns200` ya usan `POST /dragonpet` y `Host: x`; basta cambiar `Host: x` por `Host: 127.0.0.1` en esos dos helpers de test.)*

Y el tope de sesiones en `src/DragonPet.Core/ClaudeActivityTracker.cs`:

```diff
--- a/src/DragonPet.Core/ClaudeActivityTracker.cs
+++ b/src/DragonPet.Core/ClaudeActivityTracker.cs
@@ -12,6 +12,7 @@ public enum HookEventResult { Ignored, Activity, Stop }
 public sealed class ClaudeActivityTracker
 {
     public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);
+    public const int MaxSessions = 64;
 
     private enum SessionState { Thinking, Typing, NeedsYou, Finished }
 
@@ -62,6 +63,11 @@ public sealed class ClaudeActivityTracker
             {
                 return HookEventResult.Ignored;
             }
+            if (!_sessions.ContainsKey(session) && _sessions.Count >= MaxSessions)
+            {
+                var oldest = _sessions.MinBy(p => p.Value.At).Key;
+                _sessions.Remove(oldest);
+            }
             _sessions[session] = (next.Value, now);
             if (next == SessionState.Finished)
             {
```

- Test que lo demostraría: `CrossOriginPost_Returns200ButDoesNotDeliver` (cabecera `Origin: https://example.com`), `TooLarge_SubagentPreservesAgentId` (cuerpo > 1 MB con `"agent_id":"a-7"` → `ClaudeActivityTracker` devuelve `Ignored`) y `TooLarge_MalformedEscape_StillReturns200` (cuerpo > 1 MB con `"tool_name":"a\uZZZZ"` → responde `200`).

---

### 4. En PCs sin Claude Code (sin `~/.claude`), DragonPet crea `~/.claude/settings.json` al arrancar y deja `{}` al salir
- Gravedad: Importante
- Archivo: `src/DragonPet/App.xaml.cs:272-275`, `331-339`
- Escenario: Un usuario descarga DragonPet para vigilar solo Antigravity en un Windows que nunca tuvo Claude Code (no existe `%USERPROFILE%\.claude`). Al arrancar, `App.InstallHooks()` llama a `ClaudeSettingsFile.EnsureHooks`, que crea la carpeta `~/.claude` y el archivo `settings.json` con los 8 hooks. Al salir, `RemoveHooksOnExit()` borra los hooks y deja `~/.claude/settings.json` con `{}` en el disco del usuario.
- Por qué importa: Una app de solo vigilancia no debería crear carpetas ni archivos de configuración de una herramienta que el usuario no tiene instalada.
- Arreglo sugerido (parche exacto):

```diff
--- a/src/DragonPet/App.xaml.cs
+++ b/src/DragonPet/App.xaml.cs
@@ -270,10 +270,17 @@ public partial class App : Application
     }
 
     private static string? InstallHooks() =>
-        Safe(() => ClaudeSettingsFile.EnsureHooks(Path.Combine(ClaudeDir, "settings.json"))) == HookInstallOutcome.InvalidSettings
+        !Directory.Exists(ClaudeDir)
+            ? null
+            : Safe(() => ClaudeSettingsFile.EnsureHooks(Path.Combine(ClaudeDir, "settings.json"))) == HookInstallOutcome.InvalidSettings
             ? "no pude modificar settings.json de Claude"
             : null;
@@ -330,7 +337,7 @@ public partial class App : Application
     /// </summary>
     private void RemoveHooksOnExit()
     {
-        if (_hooksRemovedOnExit || _settings.HooksDisabled || _singleInstance is null)
+        if (_hooksRemovedOnExit || _settings.HooksDisabled || _singleInstance is null || !Directory.Exists(ClaudeDir))
         {
             return;
         }
```

- Test que lo demostraría: Prueba manual/integración con `ClaudeDir` inexistente: arrancar y salir con `--quit` y comprobar que no se creó `~/.claude`.

---

### 5. Higiene para GitHub: falta `LICENSE`, `README.md` asume `dist\DragonPet.exe` (ignorado en git) sin pasos previos de compilación ni avisos de WMI/desinstalación, y `--render-*` falla si no existe `out/`
- Gravedad: Importante
- Archivo: `LICENSE` (ausente), `README.md:38-113`, `src/DragonPet/Diagnostics.cs:79`, `124`, `196` y `.gitignore`
- Escenario:
  1. **Sin `LICENSE`:** sin licencia explícita en la raíz, terceros no tienen permiso legal para usar o contribuir al repositorio (el autor debe elegirla, p. ej. MIT).
  2. **Imposible seguir `README.md` en un clon limpio:** el paso 1 de `## Uso` dice `Ejecuta dist\DragonPet.exe`, pero `dist/` está en `.gitignore` y el comando `dotnet publish` está al final bajo `## Desarrollo`, sin indicar los requisitos (.NET 10 Desktop Runtime para ejecutar; .NET 10 SDK para compilar).
  3. **Faltan advertencias de transparencia y desinstalación en `README.md`:** `## Qué toca en tu PC` no menciona el uso de **WMI** (`Win32_Process` para localizar Antigravity y `Win32_Process.Create` para independizarse del job — útil ante alertas de antivirus/EDR), ni la aceptación del certificado autofirmado solo en `127.0.0.1`, ni el riesgo del refresco OAuth no oficial, ni cómo desinstalar limpiamente, ni `--render-gestures`.
  4. **Crash en `--render-*` tras clonar:** ejecutar `dist/DragonPet.exe --render-states out/states.png` (del propio README) en un clon limpio falla con `DirectoryNotFoundException` porque `out/` está en `.gitignore` y `File.Create(path)` no crea la carpeta padre.
  5. **`.gitignore` mínimo:** faltan `*.log`, `*.bak`, `*.tmp`, `.vscode/`, `.idea/` y `*.suo`.
- Por qué importa: Es la primera experiencia de quien clona el repositorio desde GitHub y evalúa si confiar en el programa.
- Arreglo sugerido (parches exactos):

1. Añadir `LICENSE` en la raíz con la licencia que elija el usuario (p. ej. MIT).
2. En `src/DragonPet/Diagnostics.cs`:

```diff
--- a/src/DragonPet/Diagnostics.cs
+++ b/src/DragonPet/Diagnostics.cs
@@ -76,7 +76,7 @@ internal static class Diagnostics
         bitmap.Render(sheet);
         var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
         encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
-        using var file = File.Create(path);
+        using var file = CreateWithParentDir(path);
         encoder.Save(file);
         return 0;
     }
@@ -121,7 +121,7 @@ internal static class Diagnostics
         var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
         encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
-        using var file = File.Create(path);
+        using var file = CreateWithParentDir(path);
         encoder.Save(file);
         return 0;
     }
@@ -193,10 +193,20 @@ internal static class Diagnostics
         var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
         encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
-        using var file = File.Create(path);
+        using var file = CreateWithParentDir(path);
         encoder.Save(file);
         return 0;
     }
 
+    private static FileStream CreateWithParentDir(string path)
+    {
+        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
+        if (!string.IsNullOrEmpty(dir))
+        {
+            Directory.CreateDirectory(dir);
+        }
+        return File.Create(path);
+    }
+
     private const int AttachParentProcess = -1;
```

3. En `.gitignore`:

```diff
--- a/.gitignore
+++ b/.gitignore
@@ -6,3 +6,9 @@ out/
 *.user
 .vs/
 .superpowers/
 TestResults/
+*.suo
+.vscode/
+.idea/
+*.log
+*.bak
+*.tmp
```

4. En `README.md` (añadir requisitos + compilación antes de `Uso`, ampliar `Qué toca en tu PC`, añadir `--render-gestures` y `Desinstalar`):

```diff
--- a/README.md
+++ b/README.md
@@ -35,9 +35,23 @@
   Ningún cambio es de golpe: los accesorios entran y salen, las caras y los colores se funden,
   y cada cambio de estado tiene un pequeño rebote.
 
+## Requisitos y compilación
+
+- **Windows 11 (x64)** con **.NET 10 Desktop Runtime** (para ejecutar) y **.NET 10 SDK** (para
+  compilar desde el código fuente: `winget install Microsoft.DotNet.SDK.10`).
+
+```bash
+dotnet test
+dotnet publish src/DragonPet -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -o dist
+```
+
+*(Si descargas un `.exe` ya compilado desde GitHub Releases, solo necesitas .NET 10 Desktop Runtime.)*
+
 ## Uso
 
-1. Ejecuta `dist\DragonPet.exe`. Aparece abajo a la derecha; puedes arrastrarlo y recordará
+1. Ejecuta `dist\DragonPet.exe` (generado arriba). Aparece abajo a la derecha; puedes arrastrarlo y recordará
    el sitio. Si lo abres desde una terminal, un editor o Claude Code, se independiza de ellos:
    cerrarlos no cierra el dragón.
@@ -51,18 +65,25 @@ Haz clic en el icono del dragón junto al reloj para:
 ## Qué toca en tu PC
 
 - **`~/.claude/settings.json`**: mientras el dragón está abierto (y si existe `~/.claude`), añade
   hooks HTTP hacia `http://127.0.0.1:47823/dragonpet` para saber cuándo Claude Code piensa, usa una
   herramienta, te pide permiso o termina. Al salir los quita y el archivo queda como estaba. La
   primera vez guarda una copia en `settings.json.dragonpet.bak`.
 - **`~/.claude/.credentials.json`**: lee tu token OAuth de Claude Code para consultar el uso. Si el
-  acceso caduca, lo renueva y guarda solo esos campos.
+  acceso caduca, lo renueva contra el endpoint OAuth de Anthropic y reescribe atómicamente solo los
+  campos de token. Nunca imprime ni registra credenciales; si Anthropic cambia el formato OAuth,
+  basta volver a ejecutar `claude` → `/login`.
+- **Servidor HTTP local (`127.0.0.1:47823`)**: escucha únicamente en loopback (no accesible desde
+  la red) y rechaza peticiones con cabecera `Origin`.
+- **WMI y servidor local de Antigravity**: consulta `Win32_Process` para leer el puerto y token
+  local del servidor de Antigravity (aceptando su certificado autofirmado únicamente en `127.0.0.1`),
+  y usa `Win32_Process.Create` una vez al arrancar desde una terminal/IDE para no cerrarse al
+  cerrar el lanzador.
 - **`%APPDATA%\DragonPet\settings.json`**: posición y preferencias.
+- **`%LOCALAPPDATA%\DragonPet\state.log`**: solo si arrancas con `--log` (sin tokens ni contenido).
 - **Inicio con Windows** (si lo activas): un valor `DragonPet` en
   `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.
 
-Las fuentes de datos no son oficiales (la API de uso de Anthropic y el servidor local de
-Antigravity). Si cambian en una actualización, el anillo correspondiente se pondrá gris.
+> **Aviso:** las fuentes de límites (API OAuth de uso de Anthropic y RPC local de Antigravity) no
+> son oficiales y pueden cambiar con futuras actualizaciones; si dejan de responder, el anillo
+> correspondiente se mostrará gris.
@@ -95,6 +116,12 @@ dist/DragonPet.exe --render-transitions out/transitions.png
 
 Dibuja fotogramas de los cambios de color, que se mezclan de forma gradual en unos 0,6 s.
 
+```bash
+dist/DragonPet.exe --render-gestures out/gestures.png
+```
+
+Dibuja la hoja de gestos y transiciones.
+
@@ -102,12 +129,14 @@ Si el dragón se cerró de golpe (por ejemplo, desde el Administrador de tareas)
 quedan instalados y Claude Code anotará un "hook error" por cada evento hasta que vuelvas a
 abrir el dragón. También puedes quitarlos desmarcando **Avisos de Claude Code (hooks)**.
 
+## Desinstalar
+
+1. Desde el icono de bandeja, desmarca **Iniciar con Windows** y pulsa **Salir** (quita los hooks
+   de `~/.claude/settings.json`).
+2. Borra `DragonPet.exe`, `%APPDATA%\DragonPet` y, si lo usaste, `%LOCALAPPDATA%\DragonPet` y
+   `~/.claude/settings.json.dragonpet.bak`.
+
 ## Desarrollo
 
 .NET 10 (WPF). `src/DragonPet.Core` contiene la lógica con tests (`dotnet test`) y
-`src/DragonPet` la parte de Windows.
-
-```bash
-dotnet publish src/DragonPet -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -o dist
-```
-
+`src/DragonPet` la parte de Windows.
```

- Test que lo demostraría: Clonar en una carpeta vacía y ejecutar en orden los comandos de `README.md` (`dotnet test`, `dotnet publish ...`, `dist\DragonPet.exe --render-states out/states.png`).

---

### 6. Resiliencia en escritorio y cierre: monitor desconectado en vivo, `RegisteredWaitHandle` sin liberar, `IOException` en `App.Log` y `ObjectDisposedException` en `PetCoordinator.Wake`
- Gravedad: Menor
- Archivo: `src/DragonPet/App.xaml.cs:46`, `86-88`, `182-192`, `341-366` y `src/DragonPet/Services/PetCoordinator.cs:99-112`
- Escenario:
  1. Si el usuario desconecta un monitor secundario mientras DragonPet corre en él, la mascota queda fuera de pantalla hasta reiniciar (`SystemEvents.DisplaySettingsChanged` no está suscrito).
  2. `ThreadPool.RegisterWaitForSingleObject` (línea 86) descarta su `RegisteredWaitHandle`, que nunca se desregistra antes de `_quitSignal.Dispose()` en `OnExit`.
  3. `SessionEnding` (línea 87) no activa `_pet.AllowClose = true`.
  4. `App.Log` (línea 190) llama a `File.AppendAllText` sin `try/catch`: si dos procesos escriben a la vez durante `--log` + relanzamiento o el archivo está bloqueado, tira una `IOException` en el hilo de UI. Y `--render-states path abc` lanza `FormatException` en `double.Parse` (línea 46).
  5. `PetCoordinator.Wake()` (línea 101) no captura `ObjectDisposedException` si llega un último callback de hook mientras `_wake` se está liberando en `DisposeAsync()`.
- Por qué importa: Evita que el dragón desaparezca al desenchufar un monitor y elimina carreras menores al cerrar o diagnosticar con `--log`.
- Arreglo sugerido (parches exactos):

```diff
--- a/src/DragonPet/App.xaml.cs
+++ b/src/DragonPet/App.xaml.cs
@@ -1,6 +1,7 @@
 using System.Globalization;
 using System.IO;
 using System.Windows;
+using Microsoft.Win32;
 using DragonPet.Core;
 using DragonPet.Rendering;
 using DragonPet.Services;
@@ -16,6 +17,7 @@ public partial class App : Application
     private const string QuitEventName = @"Local\DragonPet.Quit";
     private Mutex? _singleInstance;
     private EventWaitHandle? _quitSignal;
+    private RegisteredWaitHandle? _quitWait;
     private bool _hooksRemovedOnExit;
     private SettingsStore? _settingsStore;
     private PetSettings _settings = PetSettings.Default;
@@ -43,7 +45,7 @@ public partial class App : Application
                 Shutdown(Diagnostics.RenderTransitions(transitionsPath));
                 return;
             case ["--render-states", var path, ..]:
-                var t = e.Args.Length > 2 ? double.Parse(e.Args[2], CultureInfo.InvariantCulture) : 0.3;
+                var t = e.Args.Length > 2 && double.TryParse(e.Args[2], CultureInfo.InvariantCulture, out var parsedT) ? parsedT : 0.3;
                 Shutdown(Diagnostics.RenderStates(path, t));
                 return;
         }
@@ -83,8 +85,15 @@ public partial class App : Application
         // A 170 px window is cheap to draw in software; skipping Direct3D avoids loading GPU drivers (~tens of MB).
         System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
-        ThreadPool.RegisterWaitForSingleObject(_quitSignal, (_, _) => Dispatcher.InvokeAsync(Quit), null, Timeout.Infinite, executeOnlyOnce: true);
-        SessionEnding += (_, _) => RemoveHooksOnExit();
+        _quitWait = ThreadPool.RegisterWaitForSingleObject(_quitSignal, (_, _) => Dispatcher.InvokeAsync(Quit), null, Timeout.Infinite, executeOnlyOnce: true);
+        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
+        SessionEnding += (_, _) =>
+        {
+            if (_pet is not null)
+            {
+                _pet.AllowClose = true;
+            }
+            RemoveHooksOnExit();
+        };
@@ -185,12 +194,21 @@ public partial class App : Application
         {
             return;
         }
-        lock (_logGate) // hook events log from the server's threads, states from the UI thread
+        try
         {
-            File.AppendAllText(_logPath, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
+            lock (_logGate) // hook events log from the server's threads, states from the UI thread
+            {
+                File.AppendAllText(_logPath, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
+            }
+        }
+        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
+        {
         }
     }
 
+    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
+        Dispatcher.InvokeAsync(PlacePet);
+
     private readonly Lock _logGate = new();
@@ -340,6 +358,7 @@ public partial class App : Application
     protected override void OnExit(ExitEventArgs e)
     {
         var watch = System.Diagnostics.Stopwatch.StartNew();
+        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
         RemoveHooksOnExit();
         Log($"exit: hooks removed after {watch.ElapsedMilliseconds} ms");
         _tray?.Dispose();
@@ -361,6 +380,7 @@ public partial class App : Application
         Log($"exit: background work stopped after {watch.ElapsedMilliseconds} ms");
         _singleInstance?.Dispose();
+        _quitWait?.Unregister(null);
         _quitSignal?.Dispose();
         base.OnExit(e);
     }
```

```diff
--- a/src/DragonPet/Services/PetCoordinator.cs
+++ b/src/DragonPet/Services/PetCoordinator.cs
@@ -98,14 +98,14 @@ public sealed class PetCoordinator : IAsyncDisposable
     private void Wake()
     {
-        if (_wake.CurrentCount == 0)
+        try
         {
-            try
+            if (_wake.CurrentCount == 0)
             {
                 _wake.Release();
             }
-            catch (SemaphoreFullException)
-            {
-                // Already signalled.
-            }
+        }
+        catch (Exception ex) when (ex is SemaphoreFullException or ObjectDisposedException)
+        {
+            // Already signalled or shutting down.
         }
     }
```

- Test que lo demostraría: En `PetCoordinator`, llamar a `DisposeAsync()` seguido de `OnHookEvent("{}")` y afirmar que no lanza `ObjectDisposedException`.

---

### 7. `ClaudeHooksInstaller` sigue instalando el hook `SubagentStop` aunque `ClaudeActivityTracker` lo ignora siempre
- Gravedad: Menor
- Archivo: `src/DragonPet.Core/ClaudeHooksInstaller.cs:17-21` y `tests/DragonPet.Core.Tests/ClaudeHooksInstallerTests.cs:54-56`
- Escenario: Tras el arreglo de «¿por qué está pensando?», `ClaudeActivityTracker.cs:39` ignora incondicionalmente `SubagentStop`, pero `ClaudeHooksInstaller.Events` sigue registrando el hook HTTP de `SubagentStop`, haciendo que Claude Code dispare una petición HTTP inútil en cada sugerencia de prompt y subagente. (`Uninstall` recorre todas las claves de `root["hooks"]`, así que cualquier `SubagentStop` antiguo igualmente se limpia solo.)
- Por qué importa: Menos ruido de hooks HTTP en la sesión de Claude Code del usuario.
- Arreglo sugerido (parche exacto):

```diff
--- a/src/DragonPet.Core/ClaudeHooksInstaller.cs
+++ b/src/DragonPet.Core/ClaudeHooksInstaller.cs
@@ -17,7 +17,7 @@ public static class ClaudeHooksInstaller
     public static readonly IReadOnlyList<string> Events =
     [
         "UserPromptSubmit", "PreToolUse", "PostToolUse", "PermissionRequest",
-        "Notification", "Stop", "SubagentStop", "SessionEnd",
+        "Notification", "Stop", "SessionEnd",
     ];
```

```diff
--- a/tests/DragonPet.Core.Tests/ClaudeHooksInstallerTests.cs
+++ b/tests/DragonPet.Core.Tests/ClaudeHooksInstallerTests.cs
@@ -52,7 +52,7 @@ public class ClaudeHooksInstallerTests : IDisposable
     public void Events_AreTheSpecList()
     {
         Assert.Equal(
-            ["UserPromptSubmit", "PreToolUse", "PostToolUse", "PermissionRequest", "Notification", "Stop", "SubagentStop", "SessionEnd"],
+            ["UserPromptSubmit", "PreToolUse", "PostToolUse", "PermissionRequest", "Notification", "Stop", "SessionEnd"],
             ClaudeHooksInstaller.Events);
         Assert.Equal("http://127.0.0.1:47823/dragonpet", ClaudeHooksInstaller.HookUrl);
     }
```

- Test que lo demostraría: `Events_AreTheSpecList` + `Uninstall_RemovesLegacySubagentStopHook` (confirmando que `Uninstall` sigue eliminando un `SubagentStop` previo).

---

### 8. Huecos de test recomendados antes de publicar
- Gravedad: Menor
- Archivo: `tests/DragonPet.Core.Tests/HookServerTests.cs` y `tests/DragonPet.Core.Tests/ClaudeActivityTrackerTests.cs`
- Escenario: Con los cambios de seguridad y portabilidad anteriores conviene añadir cobertura directa para:
  1. Rechazo de `Origin:` y `Host` no loopback en `HookServer`.
  2. Conservación de `agent_id` y tolerancia a escapes inválidos en `HookServer.Essentials` (> 1 MB).
  3. Tope de `MaxSessions` en `ClaudeActivityTracker`.
  4. `ProcessCheck.AnyRunningPrefix` y `ProcessCheck.AnyRunningWithWindow`.
- Por qué importa: Protege las invariantes de seguridad y compatibilidad en otras PCs frente a regresiones futuras.
- Arreglo sugerido (bloque listo para añadir en `HookServerTests.cs` y `ClaudeActivityTrackerTests.cs`):

```csharp
// En HookServerTests.cs (y actualizar "Host: x" -> "Host: 127.0.0.1" en los helpers existentes):
[Fact]
public async Task CrossOriginPost_Returns200ButDoesNotDeliver()
{
    var port = FreePort();
    var called = false;
    await using var server = new HookServer(port, _ => called = true);
    Assert.True(server.TryStart());

    using var raw = new TcpClient();
    await raw.ConnectAsync(IPAddress.Loopback, port);
    var stream = raw.GetStream();
    await stream.WriteAsync(Encoding.ASCII.GetBytes(
        "POST /dragonpet HTTP/1.1\r\nHost: 127.0.0.1\r\nOrigin: https://evil.example\r\nContent-Length: 2\r\n\r\n{}"));
    var buf = new byte[64];
    var n = await stream.ReadAsync(buf).AsTask().WaitAsync(TimeSpan.FromSeconds(5));

    Assert.StartsWith("HTTP/1.1 200", Encoding.ASCII.GetString(buf, 0, n));
    Assert.False(called);
}

[Fact]
public async Task TooLarge_SubagentEvent_PreservesAgentIdAndHandlesBadEscape()
{
    var port = FreePort();
    var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    await using var server = new HookServer(port, body => received.TrySetResult(body));
    Assert.True(server.TryStart());
    var huge = "{\"session_id\":\"s1\",\"agent_id\":\"a-7\",\"hook_event_name\":\"PostToolUse\",\"tool_name\":\"bad\\uZZZZ\",\"tool_response\":\""
               + new string('x', 2_000_000) + "\"}";

    Assert.Equal(HttpStatusCode.OK, await PostAsync(port, huge));
    var delivered = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var tracker = new ClaudeActivityTracker();
    Assert.Equal(HookEventResult.Ignored, tracker.OnHookEvent(delivered, DateTimeOffset.UtcNow));
}

[Fact]
public void ProcessCheck_AnyRunningPrefix_MatchesNamePrefixAndFolder()
{
    using var me = Process.GetCurrentProcess();
    var prefix = me.ProcessName[..Math.Min(4, me.ProcessName.Length)];
    var folder = Path.GetFileName(Path.GetDirectoryName(me.MainModule!.FileName)!);

    Assert.True(ProcessCheck.AnyRunningPrefix(prefix, folder));
    Assert.False(ProcessCheck.AnyRunningPrefix(prefix, "definitely-not-this-folder"));
}

// En ClaudeActivityTrackerTests.cs:
[Fact]
public void ManySessions_EvictsOldestBeyondMaxSessions()
{
    var t = new ClaudeActivityTracker();
    t.OnHookEvent(Ev("PermissionRequest", "oldest", tool: "Bash"), T0);
    for (var i = 1; i <= ClaudeActivityTracker.MaxSessions; i++)
    {
        t.OnHookEvent(Ev("UserPromptSubmit", $"s{i}"), T0.AddSeconds(i));
    }

    // "oldest" (NeedsYou) was evicted once MaxSessions newer sessions arrived.
    Assert.Equal(AppActivity.Thinking, t.GetActivity(T0.AddSeconds(ClaudeActivityTracker.MaxSessions)));
}
```

---

## Listo para publicar

- **Privacidad y secretos en el repositorio:** escaneo completo de todos los archivos versionados (`.cs`, `.md`, `.json`, `.csproj`, `.sln`) sin ningún rastro de nombres de usuario (`C:\Users\…`), correos, tokens (`sk-ant`, JWT), IDs de organización reales ni credenciales; `dist/`, `out/` y `.superpowers/` ya están excluidos en `.gitignore`.
- **Protección de `~/.claude/.credentials.json`:** `ClaudeUsageClient` distingue `400`/`401` de errores transitorios (`5xx`/`429`), relee antes de bloquear ante carreras con Claude Code, retiene en memoria los refresh tokens rotados si falla la escritura, envía `scope` como Claude Code, y `ClaudeCredentials.ToString()` redacta ambos tokens; `AtomicFile` usa temporal único, `Flush(flushToDisk: true)` y `File.Replace` conservando ACLs.
- **Protección de `~/.claude/settings.json`:** fusiona sin tocar ajustes ni hooks ajenos, preserva caracteres no ASCII sin escapar, rechaza JSON con comentarios o estructura inesperada sin escribir, crea `.dragonpet.bak` una sola vez y retira los hooks al cerrar ordenadamente (`--quit`, bandeja y `SessionEnding`), solo desde la instancia propietaria del mutex.
- **Validación TLS a Antigravity:** el certificado autofirmado solo se acepta cuando `SslStream.TargetHostName == "127.0.0.1"` en el `HttpClient` privado de `AntigravityClient`; las llamadas a Anthropic usan validación TLS estándar completa.
- **Concurrencia y cierre limpio:** `PetCoordinator` separa el bucle de sondeo (red/procesos) del bucle de publicación (1 s o inmediato vía `SemaphoreSlim`), ambos y `HookServer` corren en el `ThreadPool` con `ConfigureAwait(false)`, y `App.OnExit` detiene el servidor de hooks antes que el coordinador en ~90 ms sin deadlocks.
- **Recursos y rendimiento:** renderizado `SoftwareOnly`, escena retenida con setters solo-si-cambia y sin capas offscreen innecesarias, GC en `ConserveMemory = 7`, liberación verificada de `HICON`, `HwndSource`, `Process`, `ManagementObject`, `RegistryKey` y memoria nativa (44,7 MB privados y 0,14 % de CPU medidos).
- **Escritorio y UX:** instancia única con `Local\DragonPet.SingleInstance` y cierre remoto `--quit`, `Alt+F4` oculta sin romper la bandeja, `CardWindow` click-through con tema claro/oscuro y colocación antes de mostrarse usando las áreas de trabajo reales de cada monitor (`EnumDisplayMonitors` + `GetMonitorInfo`), y 258 tests unitarios cubriendo toda la lógica de `DragonPet.Core`.
