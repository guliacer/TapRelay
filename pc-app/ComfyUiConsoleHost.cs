using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace TapRelay
{
    /// <summary>
    /// Runs the supplied ComfyUI mobile console on loopback and exposes its API
    /// from the existing TapRelay HTTP listener. The phone only uses port 1122.
    /// </summary>
    internal sealed class ComfyUiConsoleHost
    {
        private const uint KillOnJobClose = 0x00002000;
        private const int JobObjectExtendedLimitInformationClass = 9;
        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(700);
        private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(8);
        private const long MaxProxyBodyBytes = 128L * 1024 * 1024;
        private const int MaxMobileWorkflowImportBytes = 32 * 1024 * 1024;
        private const int MaxSuyanImageBytes = 64 * 1024 * 1024;
        private const string SuyanImportUrl = "http://127.0.0.1:9477/guli/suyan/import";
        private const string WorkflowTitleOverlayName = "workflow_titles.json";
        private const string AutoConvertConfigName = "auto-convert.json";
        private const string ConverterEntryName = "adapt_comfy_workflows.py";
        private const string ConverterModuleName = "comfy_native_to_api.py";
        private static readonly TimeSpan AutoConvertTimeout = TimeSpan.FromSeconds(120);

        private readonly SemaphoreSlim _startGate = new SemaphoreSlim(1, 1);
        private readonly string _dataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TapRelay",
            "ComfyUIConsole");
        private Process? _process;
        private HttpClient? _client;
        private IntPtr _jobHandle;
        private int _port;
        private Action<string> _log = _ => { };
        private Dictionary<string, List<GgControllerGroup>> _controllerGroups =
            new Dictionary<string, List<GgControllerGroup>>(StringComparer.Ordinal);
        private readonly object _promptMuteGate = new object();
        private readonly Dictionary<string, string[]> _promptMutedNodes =
            new Dictionary<string, string[]>(StringComparer.Ordinal);
        private readonly Queue<string> _promptMuteOrder = new Queue<string>();
        private readonly object _converterGate = new object();
        private Process? _converterProcess;

        public async Task StartAsync(Action<string> log)
        {
            _log = log ?? (_ => { });
            await _startGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_process != null && !_process.HasExited && await IsAvailableAsync().ConfigureAwait(false))
                    return;

                if (_process != null) await StopProcessAsync().ConfigureAwait(false);
                var exe = await PreparePayloadAsync().ConfigureAwait(false);
                _port = FindAvailableLoopbackPort();
                WritePrivateConfig(_port);
                _client = new HttpClient(new HttpClientHandler { UseProxy = false })
                {
                    BaseAddress = new Uri("http://127.0.0.1:" + _port),
                    Timeout = TimeSpan.FromMinutes(2)
                };

                _process = Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    WorkingDirectory = _dataRoot,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
                if (_process == null)
                    throw new InvalidOperationException("无法启动 ComfyUI 控制台进程");
                AttachKillOnParentExitJob(_process);

                var deadline = DateTime.UtcNow + StartTimeout;
                while (DateTime.UtcNow < deadline)
                {
                    if (await IsAvailableAsync().ConfigureAwait(false))
                    {
                        _log("ComfyUI 控制台已就绪，内部回环端口 " + _port);
                        return;
                    }
                    var process = _process;
                    if (process == null || process.HasExited)
                        throw new InvalidOperationException("ComfyUI 控制台进程提前退出，代码 " + (process?.ExitCode.ToString() ?? "未知"));
                    await Task.Delay(250).ConfigureAwait(false);
                }

                throw new InvalidOperationException("ComfyUI 控制台未能启动，请检查 PC 端运行日志");
            }
            finally
            {
                _startGate.Release();
            }
        }

        public async Task StopAsync()
        {
            await _startGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await StopProcessAsync().ConfigureAwait(false);
            }
            finally
            {
                _startGate.Release();
            }
        }

        public async Task HandleRequestAsync(HttpContext context)
        {
            var path = context.Request.Path.Value ?? string.Empty;
            var targetPath = path.Equals("/comfy", StringComparison.OrdinalIgnoreCase)
                ? "/"
                : path.Substring("/comfy".Length);
            if (string.IsNullOrWhiteSpace(targetPath)) targetPath = "/";

            var isAdmin = targetPath.Equals("/admin", StringComparison.OrdinalIgnoreCase) ||
                          targetPath.StartsWith("/api/admin/", StringComparison.OrdinalIgnoreCase);
            if (isAdmin && !IsLoopback(context.Connection.RemoteIpAddress))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync("管理页只允许在 PC 本机打开", context.RequestAborted);
                return;
            }

            if (_client == null || !await IsAvailableAsync().ConfigureAwait(false))
            {
                try
                {
                    // ComfyUI itself may be started after TapRelay. Reuse the
                    // same lifecycle gate and retry the bundled console lazily.
                    await StartAsync(_log).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    context.Response.ContentType = "application/json; charset=utf-8";
                    await context.Response.WriteAsync(
                        JsonSerializer.Serialize(new
                        {
                            error = "ComfyUI 控制台未运行：" + error.Message
                        }),
                        context.RequestAborted);
                    return;
                }
            }

            var client = _client;
            if (client == null)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsync("ComfyUI 控制台未运行", context.RequestAborted);
                return;
            }

            if (targetPath.Equals("/api/mobile/workflows/import", StringComparison.OrdinalIgnoreCase))
            {
                await HandleMobileWorkflowImportAsync(context, client).ConfigureAwait(false);
                return;
            }

            if (targetPath.Equals("/api/mobile/workflows/export", StringComparison.OrdinalIgnoreCase))
            {
                await HandleMobileWorkflowExportAsync(context).ConfigureAwait(false);
                return;
            }

            if (targetPath.Equals("/api/mobile/workflows/delete", StringComparison.OrdinalIgnoreCase))
            {
                await HandleMobileWorkflowDeleteAsync(context, client).ConfigureAwait(false);
                return;
            }

            if (targetPath.Equals("/api/mobile/workflows/rename", StringComparison.OrdinalIgnoreCase))
            {
                await HandleMobileWorkflowRenameAsync(context).ConfigureAwait(false);
                return;
            }

            if (targetPath.Equals("/api/mobile/loras", StringComparison.OrdinalIgnoreCase))
            {
                await HandleMobileLorasAsync(context).ConfigureAwait(false);
                return;
            }

            if (targetPath.Equals("/api/mobile/mask", StringComparison.OrdinalIgnoreCase))
            {
                await HandleMobileMaskAsync(context, client).ConfigureAwait(false);
                return;
            }

            if (targetPath.Equals("/api/mobile/suyan/send", StringComparison.OrdinalIgnoreCase))
            {
                await HandleMobileSuyanSendAsync(context, client).ConfigureAwait(false);
                return;
            }

            // GG 单组控制器只在前端生效。手机提交后记录哪些保存节点被关闭，
            // 轮询结果时从 job 输出里剔除，避免“图像对比”等开关名不副实。
            if (targetPath.Equals("/api/run", StringComparison.OrdinalIgnoreCase))
            {
                await HandleControllerRunAsync(context, client).ConfigureAwait(false);
                return;
            }

            if (targetPath.Equals("/api/job", StringComparison.OrdinalIgnoreCase))
            {
                await HandleControllerJobAsync(context, client).ConfigureAwait(false);
                return;
            }

            // 控制台不认识 COMFY_DYNAMICCOMBO_V3，会把动态下拉框退化成文本框。
            // 手机端工作流详情在这里读取同名 .ui.json 的 dynamicSelects 覆盖参数类型。
            if (HttpMethods.IsGet(context.Request.Method) &&
                targetPath.StartsWith("/api/workflow/", StringComparison.OrdinalIgnoreCase))
            {
                await HandleWorkflowDetailAsync(context, client, targetPath).ConfigureAwait(false);
                return;
            }

            var query = context.Request.QueryString.HasValue
                ? context.Request.QueryString.Value
                : string.Empty;
            var maxBodySize = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (maxBodySize != null && !maxBodySize.IsReadOnly)
                maxBodySize.MaxRequestBodySize = MaxProxyBodyBytes;
            using var request = new HttpRequestMessage(
                new HttpMethod(context.Request.Method), targetPath + query);

            foreach (var header in context.Request.Headers)
            {
                if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                    header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                    header.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
                    header.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                    continue;
                request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }

            if (context.Request.ContentLength.GetValueOrDefault() > 0 ||
                HttpMethods.IsPost(context.Request.Method) ||
                HttpMethods.IsPut(context.Request.Method) ||
                HttpMethods.IsPatch(context.Request.Method))
            {
                request.Content = new StreamContent(context.Request.Body);
                foreach (var header in context.Request.Headers)
                {
                    if (header.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase) &&
                        !header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                        request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
                }
                if (context.Request.ContentLength.HasValue)
                    request.Content.Headers.ContentLength = context.Request.ContentLength.Value;
            }

            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                context.RequestAborted).ConfigureAwait(false);
            context.Response.StatusCode = (int)response.StatusCode;
            foreach (var header in response.Headers)
            {
                if (!header.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase) &&
                    !header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                    context.Response.Headers[header.Key] = header.Value.ToArray();
            }

            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (string.Equals(contentType, "text/html", StringComparison.OrdinalIgnoreCase))
            {
                var html = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                // The desktop-only admin page uses root-relative API paths. Keep
                // those calls inside the /comfy proxy namespace.
                html = html.Replace("/api/", "/comfy/api/");
                html = html.Replace(
                    "127.0.0.1:8899/admin",
                    context.Request.Host.Value + "/comfy/admin");
                context.Response.ContentType = response.Content.Headers.ContentType?.ToString();
                context.Response.Headers["Cache-Control"] = "no-store";
                await context.Response.WriteAsync(html, context.RequestAborted);
                return;
            }

            foreach (var header in response.Content.Headers)
            {
                if (!header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                    context.Response.Headers[header.Key] = header.Value.ToArray();
            }
            await response.Content.CopyToAsync(context.Response.Body).ConfigureAwait(false);
        }

        private async Task HandleMobileWorkflowImportAsync(HttpContext context, HttpClient client)
        {
            if (!HttpMethods.IsPost(context.Request.Method))
            {
                context.Response.Headers["Allow"] = "POST";
                await WriteJsonAsync(context, StatusCodes.Status405MethodNotAllowed,
                    new { error = "Only POST is supported for workflow import." }).ConfigureAwait(false);
                return;
            }

            byte[] body;
            try
            {
                body = await ReadBoundedBodyAsync(
                    context, MaxMobileWorkflowImportBytes,
                    "Workflow import request exceeds 32 MB.").ConfigureAwait(false);
            }
            catch (InvalidDataException error)
            {
                await WriteJsonAsync(context, StatusCodes.Status413PayloadTooLarge,
                    new { error = error.Message }).ConfigureAwait(false);
                return;
            }

            string data;
            string filename;
            string category;
            try
            {
                using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 96 });
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty("data", out var dataElement) ||
                    dataElement.ValueKind != JsonValueKind.String ||
                    !document.RootElement.TryGetProperty("filename", out var filenameElement) ||
                    filenameElement.ValueKind != JsonValueKind.String)
                {
                    await WriteJsonAsync(context, StatusCodes.Status400BadRequest,
                        new { error = "data and filename are required." }).ConfigureAwait(false);
                    return;
                }

                data = dataElement.GetString() ?? string.Empty;
                filename = filenameElement.GetString() ?? string.Empty;
                category = document.RootElement.TryGetProperty("category", out var categoryElement) &&
                           categoryElement.ValueKind == JsonValueKind.String
                    ? categoryElement.GetString() ?? string.Empty
                    : string.Empty;
            }
            catch (JsonException)
            {
                await WriteJsonAsync(context, StatusCodes.Status400BadRequest,
                    new { error = "Import request must be valid JSON." }).ConfigureAwait(false);
                return;
            }

            var workflowBytes = Encoding.UTF8.GetBytes(data);
            if (workflowBytes.Length == 0 || workflowBytes.Length > MaxMobileWorkflowImportBytes ||
                !IsApiWorkflow(data))
            {
                await WriteJsonAsync(context, StatusCodes.Status400BadRequest,
                    new { error = "Please select a valid ComfyUI API workflow JSON file (maximum 32 MB)." }).ConfigureAwait(false);
                return;
            }

            var safeFilename = Path.GetFileName(filename.Trim());
            if (string.IsNullOrWhiteSpace(safeFilename) || safeFilename.Length > 200 ||
                !safeFilename.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                safeFilename.Any(char.IsControl) || safeFilename.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                await WriteJsonAsync(context, StatusCodes.Status400BadRequest,
                    new { error = "Workflow filename must be a valid .json filename." }).ConfigureAwait(false);
                return;
            }

            category = category.Trim();
            if (category.Length > 80) category = category.Substring(0, 80);

            var importPayload = JsonSerializer.Serialize(new
            {
                data,
                filename = safeFilename,
                category
            });
            using var requestContent = new StringContent(importPayload, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(
                "/api/admin/import", requestContent, context.RequestAborted).ConfigureAwait(false);
            await CopyResponseAsync(context, response).ConfigureAwait(false);
        }

        private async Task HandleMobileWorkflowExportAsync(HttpContext context)
        {
            if (!HttpMethods.IsGet(context.Request.Method))
            {
                context.Response.Headers["Allow"] = "GET";
                await WriteJsonAsync(context, StatusCodes.Status405MethodNotAllowed,
                    new { error = "Only GET is supported for workflow export." }).ConfigureAwait(false);
                return;
            }

            var id = context.Request.Query["id"].ToString();
            if (!IsWorkflowFileId(id))
            {
                await WriteJsonAsync(context, StatusCodes.Status400BadRequest,
                    new { error = "A valid workflow id is required." }).ConfigureAwait(false);
                return;
            }

            var workflowDirectory = Path.GetFullPath(Path.Combine(_dataRoot, "api_workflows"));
            var workflowPath = Path.GetFullPath(Path.Combine(workflowDirectory, id));
            var workflowPrefix = workflowDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!workflowPath.StartsWith(workflowPrefix, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(workflowPath))
            {
                await WriteJsonAsync(context, StatusCodes.Status404NotFound,
                    new { error = "Workflow was not found." }).ConfigureAwait(false);
                return;
            }

            var info = new FileInfo(workflowPath);
            if (info.Length > MaxProxyBodyBytes)
            {
                await WriteJsonAsync(context, StatusCodes.Status413PayloadTooLarge,
                    new { error = "Workflow file is too large to export." }).ConfigureAwait(false);
                return;
            }

            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.Headers["Content-Disposition"] = "attachment; filename*=UTF-8''" + Uri.EscapeDataString(id);
            context.Response.Headers["Cache-Control"] = "no-store";
            await using var source = File.OpenRead(workflowPath);
            await source.CopyToAsync(context.Response.Body, 81920, context.RequestAborted).ConfigureAwait(false);
        }

        private async Task HandleMobileWorkflowDeleteAsync(HttpContext context, HttpClient client)
        {
            if (!HttpMethods.IsPost(context.Request.Method))
            {
                context.Response.Headers["Allow"] = "POST";
                await WriteJsonAsync(context, StatusCodes.Status405MethodNotAllowed,
                    new { error = "Only POST is supported for workflow deletion." }).ConfigureAwait(false);
                return;
            }

            byte[] body;
            try
            {
                body = await ReadBoundedBodyAsync(
                    context, 16 * 1024, "Workflow deletion request is too large.").ConfigureAwait(false);
            }
            catch (InvalidDataException error)
            {
                await WriteJsonAsync(context, StatusCodes.Status413PayloadTooLarge,
                    new { error = error.Message }).ConfigureAwait(false);
                return;
            }

            string id;
            try
            {
                using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 8 });
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty("id", out var idElement) ||
                    idElement.ValueKind != JsonValueKind.String)
                {
                    await WriteJsonAsync(context, StatusCodes.Status400BadRequest,
                        new { error = "A workflow id is required." }).ConfigureAwait(false);
                    return;
                }
                id = idElement.GetString() ?? string.Empty;
            }
            catch (JsonException)
            {
                await WriteJsonAsync(context, StatusCodes.Status400BadRequest,
                    new { error = "Deletion request must be valid JSON." }).ConfigureAwait(false);
                return;
            }

            if (!IsWorkflowFileId(id))
            {
                await WriteJsonAsync(context, StatusCodes.Status400BadRequest,
                    new { error = "A valid workflow id is required." }).ConfigureAwait(false);
                return;
            }

            var payload = JsonSerializer.Serialize(new { id });
            using var requestContent = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(
                "/api/admin/delete", requestContent, context.RequestAborted).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) RemoveWorkflowTitleOverride(id);
            await CopyResponseAsync(context, response).ConfigureAwait(false);
        }

        private async Task HandleMobileWorkflowRenameAsync(HttpContext context)
        {
            if (!HttpMethods.IsPost(context.Request.Method))
            {
                context.Response.Headers["Allow"] = "POST";
                await WriteJsonAsync(context, StatusCodes.Status405MethodNotAllowed,
                    new { error = "Only POST is supported for workflow renaming." }).ConfigureAwait(false);
                return;
            }

            byte[] body;
            try
            {
                body = await ReadBoundedBodyAsync(
                    context, 16 * 1024, "Workflow rename request is too large.").ConfigureAwait(false);
            }
            catch (InvalidDataException error)
            {
                await WriteJsonAsync(context, StatusCodes.Status413PayloadTooLarge,
                    new { error = error.Message }).ConfigureAwait(false);
                return;
            }

            string id;
            string name;
            try
            {
                using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 8 });
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty("id", out var idElement) ||
                    idElement.ValueKind != JsonValueKind.String ||
                    !document.RootElement.TryGetProperty("name", out var nameElement) ||
                    nameElement.ValueKind != JsonValueKind.String)
                {
                    await WriteJsonAsync(context, StatusCodes.Status400BadRequest,
                        new { error = "id and name are required." }).ConfigureAwait(false);
                    return;
                }
                id = idElement.GetString() ?? string.Empty;
                name = nameElement.GetString() ?? string.Empty;
            }
            catch (JsonException)
            {
                await WriteJsonAsync(context, StatusCodes.Status400BadRequest,
                    new { error = "Rename request must be valid JSON." }).ConfigureAwait(false);
                return;
            }

            name = name.Trim();
            if (!IsWorkflowFileId(id))
            {
                await WriteJsonAsync(context, StatusCodes.Status400BadRequest,
                    new { error = "A valid workflow id is required." }).ConfigureAwait(false);
                return;
            }
            if (name.Length == 0 || name.Length > 80 || name.Any(char.IsControl))
            {
                await WriteJsonAsync(context, StatusCodes.Status400BadRequest,
                    new { error = "Workflow name must be 1-80 characters without control characters." })
                    .ConfigureAwait(false);
                return;
            }

            var uiPath = FindWorkflowUiPath(id);
            if (uiPath == null)
            {
                await WriteJsonAsync(context, StatusCodes.Status404NotFound,
                    new { error = "Workflow title file was not found." }).ConfigureAwait(false);
                return;
            }

            try
            {
                WriteWorkflowTitle(uiPath, name);
                var titles = ReadWorkflowTitleOverrides();
                titles[id] = name;
                WriteWorkflowTitleOverrides(titles);
            }
            catch (Exception error)
            {
                await WriteJsonAsync(context, StatusCodes.Status500InternalServerError,
                    new { error = "Failed to rename workflow: " + error.Message }).ConfigureAwait(false);
                return;
            }

            await WriteJsonAsync(context, StatusCodes.Status200OK, new { id, title = name })
                .ConfigureAwait(false);
        }

        /// <summary>
        /// 控制台每次请求都会重新读取 .ui.json 的 title，因此改名只需重写该字段，
        /// 保持文件名和 id 不变，已有结果索引与排序都不会失效。
        /// </summary>
        private static void WriteWorkflowTitle(string uiPath, string title)
        {
            using var document = JsonDocument.Parse(
                File.ReadAllText(uiPath, Encoding.UTF8),
                new JsonDocumentOptions { MaxDepth = 96 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Workflow title file is not a JSON object.");

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
            {
                Indented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }))
            {
                writer.WriteStartObject();
                var hasTitle = false;
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (property.NameEquals("title"))
                    {
                        writer.WriteString("title", title);
                        hasTitle = true;
                    }
                    else
                    {
                        property.WriteTo(writer);
                    }
                }
                if (!hasTitle) writer.WriteString("title", title);
                writer.WriteEndObject();
            }

            var temporary = uiPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, stream.ToArray());
                File.Move(temporary, uiPath, true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        /// <summary>
        /// 随包 .ui.json 在每次启动时会被新版资源覆盖，因此改名要额外存一份，
        /// 等 PreparePayload 刷新完资源后再回放到对应文件。
        /// </summary>
        private Dictionary<string, string> ReadWorkflowTitleOverrides()
        {
            var titles = new Dictionary<string, string>(StringComparer.Ordinal);
            var path = Path.Combine(_dataRoot, WorkflowTitleOverlayName);
            if (!File.Exists(path)) return titles;
            try
            {
                using var document = JsonDocument.Parse(
                    File.ReadAllText(path, Encoding.UTF8),
                    new JsonDocumentOptions { MaxDepth = 8 });
                if (document.RootElement.ValueKind != JsonValueKind.Object) return titles;
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (property.Value.ValueKind != JsonValueKind.String) continue;
                    if (!IsWorkflowFileId(property.Name)) continue;
                    var title = property.Value.GetString() ?? string.Empty;
                    if (title.Length > 0) titles[property.Name] = title;
                }
            }
            catch (Exception error)
            {
                _log("读取工作流名称记录失败：" + error.Message);
            }
            return titles;
        }

        private void WriteWorkflowTitleOverrides(Dictionary<string, string> titles)
        {
            Directory.CreateDirectory(_dataRoot);
            var path = Path.Combine(_dataRoot, WorkflowTitleOverlayName);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(
                    temporary,
                    JsonSerializer.Serialize(titles, WorkflowAssetJsonOptions),
                    new UTF8Encoding(false));
                File.Move(temporary, path, true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        private void RemoveWorkflowTitleOverride(string id)
        {
            try
            {
                var titles = ReadWorkflowTitleOverrides();
                if (titles.Remove(id)) WriteWorkflowTitleOverrides(titles);
            }
            catch (Exception error)
            {
                _log("清理工作流名称记录失败：" + error.Message);
            }
        }

        private void ApplyWorkflowTitleOverrides()
        {
            var titles = ReadWorkflowTitleOverrides();
            foreach (var pair in titles)
            {
                var uiPath = FindWorkflowUiPath(pair.Key);
                if (uiPath == null) continue;
                try
                {
                    if (!WorkflowTitleMatches(uiPath, pair.Value))
                        WriteWorkflowTitle(uiPath, pair.Value);
                }
                catch (Exception error)
                {
                    _log("恢复工作流名称失败：" + pair.Key + "，" + error.Message);
                }
            }
        }

        private static bool WorkflowTitleMatches(string uiPath, string expected)
        {
            try
            {
                using var document = JsonDocument.Parse(
                    File.ReadAllText(uiPath, Encoding.UTF8),
                    new JsonDocumentOptions { MaxDepth = 96 });
                return document.RootElement.ValueKind == JsonValueKind.Object &&
                       document.RootElement.TryGetProperty("title", out var title) &&
                       title.ValueKind == JsonValueKind.String &&
                       string.Equals(title.GetString(), expected, StringComparison.Ordinal);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private async Task HandleMobileLorasAsync(HttpContext context)
        {
            if (!HttpMethods.IsGet(context.Request.Method))
            {
                context.Response.Headers["Allow"] = "GET";
                await WriteJsonAsync(context, StatusCodes.Status405MethodNotAllowed,
                    new { error = "Only GET is supported for LoRA listing." }).ConfigureAwait(false);
                return;
            }

            var comfyUrl = ReadComfyUrl();
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false })
            {
                Timeout = TimeSpan.FromSeconds(5)
            };

            // The console caps select choices at 200 entries, which hides the
            // tail of large LoRA folders. Ask ComfyUI directly for the full list.
            foreach (var nodeClass in new[] { "GGLoRACustomLoader", "LoraLoaderModelOnly", "LoraLoader" })
            {
                try
                {
                    using var response = await client.GetAsync(
                        comfyUrl + "/object_info/" + nodeClass,
                        context.RequestAborted).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode) continue;

                    await using var stream = await response.Content
                        .ReadAsStreamAsync().ConfigureAwait(false);
                    using var document = await JsonDocument.ParseAsync(
                        stream,
                        new JsonDocumentOptions { MaxDepth = 64 },
                        context.RequestAborted).ConfigureAwait(false);
                    if (!TryExtractLoraNames(document.RootElement, nodeClass, out var names)) continue;

                    await WriteJsonAsync(context, StatusCodes.Status200OK, new { items = names })
                        .ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
                {
                    return;
                }
                catch
                {
                    // Try the next known LoRA loader class.
                }
            }

            await WriteJsonAsync(context, StatusCodes.Status503ServiceUnavailable, new
            {
                error = "无法从 ComfyUI 读取 LoRA 列表，请确认 ComfyUI 已启动"
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// 手机端涂抹遮罩后调用：把「原图 RGB + 遮罩 alpha」合成成一张 PNG 再传回
        /// ComfyUI 的 input 目录。LoadImage 会用 alpha 反向生成 MASK，因此遮罩区
        /// 的 alpha 压到 1（几乎透明但非零），其余保持 255，同时保留完整 RGB，
        /// 避免浏览器/画布导出透明图时把遮罩区的颜色清成黑或白。
        /// </summary>
        private async Task HandleMobileMaskAsync(HttpContext context, HttpClient client)
        {
            if (!HttpMethods.IsPost(context.Request.Method))
            {
                context.Response.Headers["Allow"] = "POST";
                await WriteJsonAsync(context, StatusCodes.Status405MethodNotAllowed,
                    new { error = "遮罩合成只支持 POST。" }).ConfigureAwait(false);
                return;
            }

            byte[] body;
            try
            {
                body = await ReadBoundedBodyAsync(
                    context, 64 * 1024, "遮罩合成请求过大。").ConfigureAwait(false);
            }
            catch (InvalidDataException error)
            {
                await WriteJsonAsync(context, StatusCodes.Status413PayloadTooLarge,
                    new { error = error.Message }).ConfigureAwait(false);
                return;
            }

            string baseName;
            string maskName;
            try
            {
                using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 8 });
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty("base", out var baseElement) ||
                    baseElement.ValueKind != JsonValueKind.String ||
                    !document.RootElement.TryGetProperty("mask", out var maskElement) ||
                    maskElement.ValueKind != JsonValueKind.String)
                {
                    await WriteJsonAsync(context, StatusCodes.Status400BadRequest,
                        new { error = "缺少 base / mask 文件名。" }).ConfigureAwait(false);
                    return;
                }
                baseName = baseElement.GetString() ?? string.Empty;
                maskName = maskElement.GetString() ?? string.Empty;
            }
            catch (JsonException)
            {
                await WriteJsonAsync(context, StatusCodes.Status400BadRequest,
                    new { error = "遮罩合成请求必须是有效 JSON。" }).ConfigureAwait(false);
                return;
            }

            if (!IsSafeImageName(baseName) || !IsSafeImageName(maskName))
            {
                await WriteJsonAsync(context, StatusCodes.Status400BadRequest,
                    new { error = "原图或遮罩文件名不合法。" }).ConfigureAwait(false);
                return;
            }

            const int maxImageBytes = 32 * 1024 * 1024;
            byte[] baseBytes;
            byte[] maskBytes;
            try
            {
                baseBytes = await DownloadInputImageAsync(
                    client, baseName, maxImageBytes, context.RequestAborted).ConfigureAwait(false);
                maskBytes = await DownloadInputImageAsync(
                    client, maskName, maxImageBytes, context.RequestAborted).ConfigureAwait(false);
            }
            catch (InvalidDataException error)
            {
                await WriteJsonAsync(context, StatusCodes.Status413PayloadTooLarge,
                    new { error = error.Message }).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                await WriteJsonAsync(context, StatusCodes.Status502BadGateway,
                    new { error = "读取原图或遮罩失败：" + error.Message }).ConfigureAwait(false);
                return;
            }

            byte[] composed;
            try
            {
                composed = ComposeMaskPng(baseBytes, maskBytes);
            }
            catch (Exception error)
            {
                await WriteJsonAsync(context, StatusCodes.Status400BadRequest,
                    new { error = "遮罩合成失败：" + error.Message }).ConfigureAwait(false);
                return;
            }

            var composedName = Path.GetFileNameWithoutExtension(baseName) + "_masked.png";
            try
            {
                using var upload = new HttpRequestMessage(HttpMethod.Post, "/api/upload");
                upload.Headers.TryAddWithoutValidation("X-Filename", Uri.EscapeDataString(composedName));
                upload.Content = new ByteArrayContent(composed);
                upload.Content.Headers.TryAddWithoutValidation("Content-Type", "application/octet-stream");
                using var response = await client.SendAsync(upload, context.RequestAborted).ConfigureAwait(false);
                var responseText = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    await WriteJsonAsync(context, StatusCodes.Status502BadGateway, new
                    {
                        error = "上传合成图失败：" + TruncateForMessage(responseText)
                    }).ConfigureAwait(false);
                    return;
                }

                var uploadedName = composedName;
                try
                {
                    using var document = JsonDocument.Parse(responseText);
                    if (document.RootElement.ValueKind == JsonValueKind.Object &&
                        document.RootElement.TryGetProperty("name", out var nameElement) &&
                        nameElement.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(nameElement.GetString()))
                        uploadedName = nameElement.GetString();
                }
                catch (JsonException)
                {
                    // 控制台返回非 JSON 时仍按约定文件名处理。
                }

                await WriteJsonAsync(context, StatusCodes.Status200OK, new { name = uploadedName })
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
            }
            catch (Exception error)
            {
                await WriteJsonAsync(context, StatusCodes.Status502BadGateway,
                    new { error = "上传合成图失败：" + error.Message }).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// 手机端结果卡片的「发送到素言」：从 ComfyUI 取回结果图，再以
        /// multipart/form-data 推送到素言本机收件服务，与 GuliNodes 画布按钮一致。
        /// </summary>
        private async Task HandleMobileSuyanSendAsync(HttpContext context, HttpClient client)
        {
            if (!HttpMethods.IsPost(context.Request.Method))
            {
                context.Response.Headers["Allow"] = "POST";
                await WriteJsonAsync(context, StatusCodes.Status405MethodNotAllowed,
                    new { error = "发送到素言只支持 POST。" }).ConfigureAwait(false);
                return;
            }

            byte[] body;
            try
            {
                body = await ReadBoundedBodyAsync(
                    context, 16 * 1024, "发送到素言请求过大。").ConfigureAwait(false);
            }
            catch (InvalidDataException error)
            {
                await WriteJsonAsync(context, StatusCodes.Status413PayloadTooLarge,
                    new { error = error.Message }).ConfigureAwait(false);
                return;
            }

            string filename;
            var subfolder = string.Empty;
            var type = "output";
            var prompt = string.Empty;
            var title = string.Empty;
            try
            {
                using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 8 });
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("filename", out var filenameElement) ||
                    filenameElement.ValueKind != JsonValueKind.String)
                {
                    await WriteJsonAsync(context, StatusCodes.Status400BadRequest,
                        new { error = "缺少 filename。" }).ConfigureAwait(false);
                    return;
                }
                filename = (filenameElement.GetString() ?? string.Empty).Trim();
                if (root.TryGetProperty("subfolder", out var subfolderElement) &&
                    subfolderElement.ValueKind == JsonValueKind.String)
                    subfolder = (subfolderElement.GetString() ?? string.Empty).Trim();
                if (root.TryGetProperty("type", out var typeElement) &&
                    typeElement.ValueKind == JsonValueKind.String)
                    type = (typeElement.GetString() ?? string.Empty).Trim().ToLowerInvariant();
                if (root.TryGetProperty("prompt", out var promptElement) &&
                    promptElement.ValueKind == JsonValueKind.String)
                    prompt = promptElement.GetString() ?? string.Empty;
                if (root.TryGetProperty("title", out var titleElement) &&
                    titleElement.ValueKind == JsonValueKind.String)
                    title = titleElement.GetString() ?? string.Empty;
            }
            catch (JsonException)
            {
                await WriteJsonAsync(context, StatusCodes.Status400BadRequest,
                    new { error = "发送到素言请求必须是有效 JSON。" }).ConfigureAwait(false);
                return;
            }

            if (!IsSafeImageName(filename) || !IsSafeImageSubfolder(subfolder) ||
                (type != "output" && type != "temp" && type != "input"))
            {
                await WriteJsonAsync(context, StatusCodes.Status400BadRequest,
                    new { error = "图片文件名、子目录或类型不合法。" }).ConfigureAwait(false);
                return;
            }
            if (prompt.Length > 4000) prompt = prompt.Substring(0, 4000);
            if (title.Length > 100) title = title.Substring(0, 100);

            byte[] imageBytes;
            string responseType;
            try
            {
                var query = "/api/view?filename=" + Uri.EscapeDataString(filename) +
                            "&subfolder=" + Uri.EscapeDataString(subfolder) +
                            "&type=" + Uri.EscapeDataString(type);
                using var response = await client.GetAsync(
                    query, HttpCompletionOption.ResponseHeadersRead,
                    context.RequestAborted).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    await WriteJsonAsync(context, StatusCodes.Status502BadGateway,
                        new { error = "读取结果图片失败：" + (int)response.StatusCode }).ConfigureAwait(false);
                    return;
                }
                responseType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
                if (response.Content.Headers.ContentLength.GetValueOrDefault() > MaxSuyanImageBytes)
                {
                    await WriteJsonAsync(context, StatusCodes.Status413PayloadTooLarge,
                        new { error = "图片超过 64 MB，无法发送到素言。" }).ConfigureAwait(false);
                    return;
                }

                await using var stream = await response.Content
                    .ReadAsStreamAsync().ConfigureAwait(false);
                using var output = new MemoryStream();
                var buffer = new byte[81920];
                int read;
                while ((read = await stream.ReadAsync(
                           buffer, 0, buffer.Length, context.RequestAborted).ConfigureAwait(false)) > 0)
                {
                    if (output.Length + read > MaxSuyanImageBytes)
                    {
                        await WriteJsonAsync(context, StatusCodes.Status413PayloadTooLarge,
                            new { error = "图片超过 64 MB，无法发送到素言。" }).ConfigureAwait(false);
                        return;
                    }
                    output.Write(buffer, 0, read);
                }
                imageBytes = output.ToArray();
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                await WriteJsonAsync(context, StatusCodes.Status502BadGateway,
                    new { error = "读取结果图片失败：" + error.Message }).ConfigureAwait(false);
                return;
            }

            var mediaType = ResolveSuyanImageContentType(responseType, filename);
            var uploadName = "comfyui_001" + ExtensionForImageContentType(mediaType);
            try
            {
                using var suyan = new HttpClient(new HttpClientHandler { UseProxy = false })
                {
                    Timeout = TimeSpan.FromSeconds(90)
                };
                if (!string.IsNullOrWhiteSpace(prompt) && string.IsNullOrWhiteSpace(title))
                    title = prompt.Length <= 24 ? prompt : prompt.Substring(0, 24);
                // 素言收件端按 name="..." / filename="..." 的引号形式解析 multipart；
                // .NET 的 MultipartFormDataContent 默认不加引号，会被当成纯文本字段。
                var boundary = "----TapRelay" + Guid.NewGuid().ToString("N");
                var multipartBody = BuildSuyanMultipartBody(
                    boundary, imageBytes, mediaType, uploadName, prompt, title);
                using var content = new ByteArrayContent(multipartBody);
                content.Headers.TryAddWithoutValidation(
                    "Content-Type", "multipart/form-data; boundary=" + boundary);

                using var response = await suyan.PostAsync(
                    SuyanImportUrl, content, context.RequestAborted).ConfigureAwait(false);
                var responseText = await response.Content
                    .ReadAsStringAsync().ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    await WriteJsonAsync(context, StatusCodes.Status502BadGateway, new
                    {
                        error = "素言收件失败(" + (int)response.StatusCode + ")：" +
                                TruncateForMessage(responseText)
                    }).ConfigureAwait(false);
                    return;
                }

                _log("已发送到素言：" + filename + "（" + imageBytes.Length + " 字节）");
                await WriteJsonAsync(context, StatusCodes.Status200OK, new
                {
                    ok = true,
                    sentImages = 1
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
            }
            catch (OperationCanceledException)
            {
                await WriteJsonAsync(context, StatusCodes.Status504GatewayTimeout,
                    new { error = "连接素言超时，请确认素言已启动。" }).ConfigureAwait(false);
            }
            catch (HttpRequestException error)
            {
                await WriteJsonAsync(context, StatusCodes.Status502BadGateway, new
                {
                    error = "无法连接素言收件服务（请确认素言已启动且已开启本机收件）：" +
                            error.Message
                }).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                await WriteJsonAsync(context, StatusCodes.Status502BadGateway,
                    new { error = "发送到素言失败：" + error.Message }).ConfigureAwait(false);
            }
        }

        private static bool IsSafeImageSubfolder(string subfolder)
        {
            if (string.IsNullOrEmpty(subfolder)) return true;
            if (subfolder.Length > 200) return false;
            var normalized = subfolder.Replace('\\', '/');
            if (normalized.StartsWith("/", StringComparison.Ordinal) ||
                normalized.Contains("..") || normalized.IndexOf(':') >= 0 ||
                normalized.Any(char.IsControl))
                return false;
            foreach (var part in normalized.Split('/'))
            {
                if (part.Length == 0 || part == "." ||
                    part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    return false;
            }
            return true;
        }

        private static string ResolveSuyanImageContentType(string contentType, string filename)
        {
            var mediaType = (contentType ?? string.Empty).Split(';')[0].Trim();
            if (mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                return mediaType;
            switch (Path.GetExtension(filename).ToLowerInvariant())
            {
                case ".jpg":
                case ".jpeg":
                    return "image/jpeg";
                case ".webp":
                    return "image/webp";
                case ".gif":
                    return "image/gif";
                case ".bmp":
                    return "image/bmp";
                default:
                    return "image/png";
            }
        }

        private static string ExtensionForImageContentType(string mediaType)
        {
            switch ((mediaType ?? string.Empty).ToLowerInvariant())
            {
                case "image/jpeg":
                case "image/jpg":
                    return ".jpg";
                case "image/webp":
                    return ".webp";
                case "image/gif":
                    return ".gif";
                case "image/bmp":
                    return ".bmp";
                default:
                    return ".png";
            }
        }

        private static byte[] BuildSuyanMultipartBody(
            string boundary, byte[] imageBytes, string mediaType, string uploadName,
            string prompt, string title)
        {
            using var output = new MemoryStream();
            void WriteText(string text)
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                output.Write(bytes, 0, bytes.Length);
            }

            WriteText("--" + boundary + "\r\n");
            WriteText("Content-Disposition: form-data; name=\"generationMethod\"\r\n\r\n");
            WriteText("comfyui\r\n");
            if (!string.IsNullOrWhiteSpace(prompt))
            {
                WriteText("--" + boundary + "\r\n");
                WriteText("Content-Disposition: form-data; name=\"prompt\"\r\n\r\n");
                WriteText(prompt + "\r\n");
            }
            if (!string.IsNullOrWhiteSpace(title))
            {
                WriteText("--" + boundary + "\r\n");
                WriteText("Content-Disposition: form-data; name=\"title\"\r\n\r\n");
                WriteText(title + "\r\n");
            }
            WriteText("--" + boundary + "\r\n");
            WriteText("Content-Disposition: form-data; name=\"images\"; filename=\"" +
                      uploadName + "\"\r\n");
            WriteText("Content-Type: " + mediaType + "\r\n\r\n");
            output.Write(imageBytes, 0, imageBytes.Length);
            WriteText("\r\n--" + boundary + "--\r\n");
            return output.ToArray();
        }

        private async Task HandleWorkflowDetailAsync(
            HttpContext context, HttpClient client, string targetPath)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, targetPath);
            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead,
                context.RequestAborted).ConfigureAwait(false);
            var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                try
                {
                    responseBody = ApplyDynamicSelects(responseBody);
                }
                catch (JsonException)
                {
                    // 控制台返回非预期 JSON 时原样透传。
                }
            }

            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.ContentType = response.Content.Headers.ContentType?.ToString()
                                            ?? "application/json; charset=utf-8";
            context.Response.Headers["Cache-Control"] = "no-store";
            await context.Response.WriteAsync(responseBody, context.RequestAborted).ConfigureAwait(false);
        }

        /// <summary>
        /// 把 .ui.json 的 dynamicSelects 合并进工作流详情的参数：
        /// 指定键的 kind 改为 select，并写入 choices。仅影响手机端展示，
        /// 不修改控制台或工作流文件本身。
        /// </summary>
        private string ApplyDynamicSelects(string json)
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 128 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("id", out var idElement) ||
                idElement.ValueKind != JsonValueKind.String)
            {
                return json;
            }

            var uiPath = FindWorkflowUiPath(idElement.GetString() ?? string.Empty);
            if (uiPath == null) return json;

            using var uiDocument = JsonDocument.Parse(
                File.ReadAllText(uiPath), new JsonDocumentOptions { MaxDepth = 32 });
            if (!uiDocument.RootElement.TryGetProperty("dynamicSelects", out var selectsElement) ||
                selectsElement.ValueKind != JsonValueKind.Object)
            {
                return json;
            }

            var selects = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var select in selectsElement.EnumerateObject())
            {
                if (select.Value.ValueKind != JsonValueKind.Array) continue;
                var choices = select.Value.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString() ?? string.Empty)
                    .Where(item => item.Length > 0)
                    .ToList();
                if (choices.Count > 0) selects[select.Name] = choices;
            }
            if (selects.Count == 0) return json;

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(
                       stream, new JsonWriterOptions
                       {
                           Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                       }))
            {
                writer.WriteStartObject();
                foreach (var property in root.EnumerateObject())
                {
                    if (!property.NameEquals("params") ||
                        property.Value.ValueKind != JsonValueKind.Object)
                    {
                        property.WriteTo(writer);
                        continue;
                    }

                    writer.WritePropertyName("params");
                    writer.WriteStartObject();
                    foreach (var group in property.Value.EnumerateObject())
                    {
                        if ((!group.NameEquals("primary") && !group.NameEquals("advanced")) ||
                            group.Value.ValueKind != JsonValueKind.Array)
                        {
                            group.WriteTo(writer);
                            continue;
                        }

                        writer.WritePropertyName(group.Name);
                        writer.WriteStartArray();
                        foreach (var param in group.Value.EnumerateArray())
                        {
                            if (param.ValueKind != JsonValueKind.Object ||
                                !param.TryGetProperty("key", out var keyElement) ||
                                keyElement.ValueKind != JsonValueKind.String ||
                                !selects.TryGetValue(keyElement.GetString() ?? string.Empty,
                                    out var choices))
                            {
                                param.WriteTo(writer);
                                continue;
                            }

                            var hasKind = false;
                            var hasChoices = false;
                            writer.WriteStartObject();
                            foreach (var field in param.EnumerateObject())
                            {
                                if (field.NameEquals("kind"))
                                {
                                    writer.WriteString("kind", "select");
                                    hasKind = true;
                                }
                                else if (field.NameEquals("choices"))
                                {
                                    WriteChoices(writer, choices);
                                    hasChoices = true;
                                }
                                else
                                {
                                    field.WriteTo(writer);
                                }
                            }
                            if (!hasKind) writer.WriteString("kind", "select");
                            if (!hasChoices) WriteChoices(writer, choices);
                            writer.WriteEndObject();
                        }
                        writer.WriteEndArray();
                    }
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
            }
            return Encoding.UTF8.GetString(stream.ToArray());
        }

        private static void WriteChoices(Utf8JsonWriter writer, List<string> choices)
        {
            writer.WritePropertyName("choices");
            writer.WriteStartArray();
            foreach (var choice in choices) writer.WriteStringValue(choice);
            writer.WriteEndArray();
        }

        /// <summary>把工作流 id 映射到同目录下的 .ui.json；不存在或越界时返回 null。</summary>
        private string? FindWorkflowUiPath(string id)
        {
            var decoded = Uri.UnescapeDataString(id);
            if (!IsWorkflowFileId(decoded)) return null;
            var uiName = decoded.Substring(0, decoded.Length - ".api.json".Length) + ".ui.json";
            var directory = Path.GetFullPath(Path.Combine(_dataRoot, "api_workflows"));
            var path = Path.GetFullPath(Path.Combine(directory, uiName));
            var prefix = directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && File.Exists(path)
                ? path
                : null;
        }

        private async Task HandleControllerRunAsync(HttpContext context, HttpClient client)
        {
            if (!HttpMethods.IsPost(context.Request.Method))
            {
                using var wrongMethod = new HttpRequestMessage(
                    new HttpMethod(context.Request.Method), "/api/run");
                await ProxySimpleResponseAsync(context, client, wrongMethod).ConfigureAwait(false);
                return;
            }

            byte[] body;
            try
            {
                body = await ReadBoundedBodyAsync(
                    context, 8 * 1024 * 1024, "工作流运行请求过大。").ConfigureAwait(false);
            }
            catch (InvalidDataException error)
            {
                await WriteJsonAsync(context, StatusCodes.Status413PayloadTooLarge,
                    new { error = error.Message }).ConfigureAwait(false);
                return;
            }

            body = ApplyWorkflowRunOverrides(body);
            var mutedNodes = ResolveMutedNodes(body);
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/run");
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.TryAddWithoutValidation(
                "Content-Type",
                context.Request.ContentType ?? "application/json; charset=utf-8");

            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead,
                context.RequestAborted).ConfigureAwait(false);
            var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var promptId = ReadPromptId(responseBody);
            if (!string.IsNullOrEmpty(promptId) && mutedNodes.Count > 0)
            {
                RememberPromptMutes(promptId!, mutedNodes);
                _log("GG 单组控制任务 " + promptId + "，隐藏节点 " + string.Join(",", mutedNodes));
            }

            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(responseBody, context.RequestAborted).ConfigureAwait(false);
        }

        private async Task HandleControllerJobAsync(HttpContext context, HttpClient client)
        {
            var query = context.Request.QueryString.HasValue
                ? context.Request.QueryString.Value
                : string.Empty;
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/job" + query);
            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead,
                context.RequestAborted).ConfigureAwait(false);
            var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            var promptId = context.Request.Query["id"].ToString();
            var mutedNodes = string.IsNullOrEmpty(promptId) ? null : TryGetPromptMutes(promptId);
            if (mutedNodes != null && mutedNodes.Length > 0)
            {
                try
                {
                    responseBody = FilterJobResponse(responseBody, mutedNodes);
                }
                catch (JsonException)
                {
                    // 控制台返回非 JSON 时原样透传。
                }
            }

            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.ContentType = response.Content.Headers.ContentType?.ToString()
                                            ?? "application/json; charset=utf-8";
            await context.Response.WriteAsync(responseBody, context.RequestAborted).ConfigureAwait(false);
        }

        private async Task ProxySimpleResponseAsync(
            HttpContext context, HttpClient client, HttpRequestMessage request)
        {
            using (request)
            using (var response = await client.SendAsync(
                       request, HttpCompletionOption.ResponseHeadersRead,
                       context.RequestAborted).ConfigureAwait(false))
            {
                context.Response.StatusCode = (int)response.StatusCode;
                if (response.Content.Headers.ContentType != null)
                    context.Response.ContentType = response.Content.Headers.ContentType.ToString();
                await response.Content.CopyToAsync(context.Response.Body).ConfigureAwait(false);
            }
        }

        /// <summary>根据本次提交的开关值，返回需要从结果里隐藏的节点 ID。</summary>
        private List<string> ResolveMutedNodes(byte[] body)
        {
            var muted = new List<string>();
            Dictionary<string, JsonElement>? root;
            try
            {
                root = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                    Encoding.UTF8.GetString(body));
            }
            catch (JsonException)
            {
                return muted;
            }
            if (root == null) return muted;

            var workflowId = root.TryGetValue("id", out var idElement) &&
                             idElement.ValueKind == JsonValueKind.String
                ? idElement.GetString()
                : null;
            if (string.IsNullOrEmpty(workflowId) ||
                !_controllerGroups.TryGetValue(workflowId!, out var groups))
            {
                return muted;
            }

            Dictionary<string, JsonElement>? values = null;
            if (root.TryGetValue("values", out var valuesElement) &&
                valuesElement.ValueKind == JsonValueKind.Object)
            {
                try
                {
                    values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                        valuesElement.GetRawText());
                }
                catch (JsonException)
                {
                    values = null;
                }
            }

            foreach (var group in groups)
            {
                var enabled = true;
                if (values != null && values.TryGetValue(group.OptionKey, out var value))
                    enabled = !IsFalseValue(value);
                if (enabled) continue;
                foreach (var node in group.MutedNodes)
                    if (!muted.Contains(node)) muted.Add(node);
            }
            return muted;
        }

        private static bool IsFalseValue(JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.False:
                    return true;
                case JsonValueKind.True:
                    return false;
                case JsonValueKind.Number:
                    return value.TryGetDouble(out var number) && number == 0;
                case JsonValueKind.String:
                    var text = value.GetString()?.Trim();
                    return string.Equals(text, "false", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(text, "0", StringComparison.Ordinal) ||
                           string.Equals(text, "否", StringComparison.Ordinal) ||
                           string.Equals(text, "关闭", StringComparison.Ordinal);
                default:
                    return false;
            }
        }

        /// <summary>
        /// 按 .ui.json 的 dependentValues 在提交前写入联动参数：例如“处理模式”
        /// 选择解码/混淆时，自动使用对应保存节点的路径，无需手机端手动填写。
        /// </summary>
        private byte[] ApplyWorkflowRunOverrides(byte[] body)
        {
            Dictionary<string, JsonElement>? root;
            try
            {
                root = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                    Encoding.UTF8.GetString(body));
            }
            catch (JsonException)
            {
                return body;
            }
            if (root == null) return body;

            var workflowId = root.TryGetValue("id", out var idElement) &&
                             idElement.ValueKind == JsonValueKind.String
                ? idElement.GetString()
                : null;
            if (string.IsNullOrEmpty(workflowId)) return body;

            var uiPath = FindWorkflowUiPath(workflowId!);
            if (uiPath == null) return body;
            if (!root.TryGetValue("values", out var valuesElement) ||
                valuesElement.ValueKind != JsonValueKind.Object)
            {
                return body;
            }

            Dictionary<string, JsonElement>? values;
            try
            {
                values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                    valuesElement.GetRawText());
            }
            catch (JsonException)
            {
                return body;
            }
            if (values == null) return body;

            var overrides = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                CollectDependentValues(uiPath, values, overrides);
            }
            catch (JsonException)
            {
                return body;
            }
            if (overrides.Count == 0) return body;

            using var document = JsonDocument.Parse(Encoding.UTF8.GetString(body));
            if (document.RootElement.ValueKind != JsonValueKind.Object) return body;

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(
                       stream, new JsonWriterOptions
                       {
                           Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                       }))
            {
                writer.WriteStartObject();
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (!property.NameEquals("values") ||
                        property.Value.ValueKind != JsonValueKind.Object)
                    {
                        property.WriteTo(writer);
                        continue;
                    }

                    writer.WritePropertyName("values");
                    writer.WriteStartObject();
                    var written = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var value in property.Value.EnumerateObject())
                    {
                        if (overrides.TryGetValue(value.Name, out var replacement))
                        {
                            writer.WriteString(value.Name, replacement);
                            written.Add(value.Name);
                        }
                        else
                        {
                            value.WriteTo(writer);
                        }
                    }
                    foreach (var pair in overrides)
                    {
                        if (!written.Contains(pair.Key))
                            writer.WriteString(pair.Key, pair.Value);
                    }
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
            }

            _log("工作流联动参数：" + workflowId + " → " +
                 string.Join("；", overrides.Select(pair => pair.Key + "=" + pair.Value)));
            return stream.ToArray();
        }

        private static void CollectDependentValues(
            string uiPath,
            Dictionary<string, JsonElement> values,
            Dictionary<string, string> overrides)
        {
            using var uiDocument = JsonDocument.Parse(
                File.ReadAllText(uiPath), new JsonDocumentOptions { MaxDepth = 32 });
            if (!uiDocument.RootElement.TryGetProperty("dependentValues", out var dependencies) ||
                dependencies.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (var dependency in dependencies.EnumerateObject())
            {
                if (dependency.Value.ValueKind != JsonValueKind.Object ||
                    !dependency.Value.TryGetProperty("source", out var source) ||
                    source.ValueKind != JsonValueKind.String ||
                    !dependency.Value.TryGetProperty("values", out var mapping) ||
                    mapping.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var sourceKey = source.GetString() ?? string.Empty;
                if (!values.TryGetValue(sourceKey, out var sourceValue)) continue;
                var sourceText = ReadScalarText(sourceValue);
                if (string.IsNullOrEmpty(sourceText)) continue;

                foreach (var entry in mapping.EnumerateObject())
                {
                    if (!string.Equals(entry.Name, sourceText, StringComparison.Ordinal) ||
                        entry.Value.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }
                    overrides[dependency.Name] = entry.Value.GetString() ?? string.Empty;
                    break;
                }
            }
        }

        private static string? ReadScalarText(JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    return value.GetString();
                case JsonValueKind.Number:
                    return value.GetRawText();
                case JsonValueKind.True:
                    return "true";
                case JsonValueKind.False:
                    return "false";
                default:
                    return null;
            }
        }

        private static string? ReadPromptId(string json)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                return document.RootElement.ValueKind == JsonValueKind.Object &&
                       document.RootElement.TryGetProperty("prompt_id", out var promptId) &&
                       promptId.ValueKind == JsonValueKind.String
                    ? promptId.GetString()
                    : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private void RememberPromptMutes(string promptId, List<string> mutedNodes)
        {
            lock (_promptMuteGate)
            {
                if (_promptMutedNodes.ContainsKey(promptId)) return;
                _promptMutedNodes[promptId] = mutedNodes.ToArray();
                _promptMuteOrder.Enqueue(promptId);
                while (_promptMuteOrder.Count > 200)
                    _promptMutedNodes.Remove(_promptMuteOrder.Dequeue());
            }
        }

        private string[]? TryGetPromptMutes(string promptId)
        {
            lock (_promptMuteGate)
            {
                return _promptMutedNodes.TryGetValue(promptId, out var muted) ? muted : null;
            }
        }

        private static string FilterJobResponse(string json, string[] mutedNodes)
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 128 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) return json;

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(
                       stream, new JsonWriterOptions
                       {
                           Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                       }))
            {
                writer.WriteStartObject();
                foreach (var job in document.RootElement.EnumerateObject())
                {
                    writer.WritePropertyName(job.Name);
                    if (job.Value.ValueKind != JsonValueKind.Object)
                    {
                        job.Value.WriteTo(writer);
                        continue;
                    }

                    writer.WriteStartObject();
                    foreach (var property in job.Value.EnumerateObject())
                    {
                        if (!property.NameEquals("outputs") ||
                            property.Value.ValueKind != JsonValueKind.Object)
                        {
                            property.WriteTo(writer);
                            continue;
                        }

                        writer.WritePropertyName("outputs");
                        writer.WriteStartObject();
                        foreach (var output in property.Value.EnumerateObject())
                        {
                            if (Array.IndexOf(mutedNodes, output.Name) >= 0) continue;
                            output.WriteTo(writer);
                        }
                        writer.WriteEndObject();
                    }
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
            }
            return Encoding.UTF8.GetString(stream.ToArray());
        }

        private static bool IsSafeImageName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 200 ||
                !string.Equals(name, Path.GetFileName(name), StringComparison.Ordinal) ||
                name.Any(char.IsControl) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return false;

            var extension = Path.GetExtension(name).ToLowerInvariant();
            return extension == ".png" || extension == ".jpg" || extension == ".jpeg" ||
                   extension == ".webp" || extension == ".bmp";
        }

        private static async Task<byte[]> DownloadInputImageAsync(
            HttpClient client, string name, int maxBytes, CancellationToken cancellationToken)
        {
            var url = "/api/view?filename=" + Uri.EscapeDataString(name) + "&type=input";
            using var response = await client.GetAsync(
                url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException("ComfyUI 的 input 目录里找不到图片：" + name);

            if (response.Content.Headers.ContentLength.GetValueOrDefault() > maxBytes)
                throw new InvalidDataException("图片超过 32 MB，无法合成遮罩。");

            await using var stream = await response.Content
                .ReadAsStreamAsync().ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(
                       buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (output.Length + read > maxBytes)
                    throw new InvalidDataException("图片超过 32 MB，无法合成遮罩。");
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }

        private static byte[] ComposeMaskPng(byte[] baseBytes, byte[] maskBytes)
        {
            using var baseSource = new Bitmap(new MemoryStream(baseBytes));
            using var maskSource = new Bitmap(new MemoryStream(maskBytes));
            var width = baseSource.Width;
            var height = baseSource.Height;
            if (width <= 0 || height <= 0 || (long)width * height > 64L * 1024 * 1024)
                throw new InvalidOperationException("图片尺寸不受支持。");

            using var output = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(output))
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.DrawImage(
                    baseSource,
                    new Rectangle(0, 0, width, height),
                    0, 0, baseSource.Width, baseSource.Height,
                    GraphicsUnit.Pixel);
            }

            using var maskScaled = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(maskScaled))
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.DrawImage(
                    maskSource,
                    new Rectangle(0, 0, width, height),
                    0, 0, maskSource.Width, maskSource.Height,
                    GraphicsUnit.Pixel);
            }

            var rectangle = new Rectangle(0, 0, width, height);
            var outputData = output.LockBits(rectangle, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            var maskData = maskScaled.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var outputStride = outputData.Stride;
                var maskStride = maskData.Stride;
                var outputBuffer = new byte[outputStride * height];
                var maskBuffer = new byte[maskStride * height];
                Marshal.Copy(outputData.Scan0, outputBuffer, 0, outputBuffer.Length);
                Marshal.Copy(maskData.Scan0, maskBuffer, 0, maskBuffer.Length);

                for (var y = 0; y < height; y++)
                {
                    var outputRow = y * outputStride;
                    var maskRow = y * maskStride;
                    for (var x = 0; x < width; x++)
                    {
                        var outputIndex = outputRow + x * 4;
                        var maskIndex = maskRow + x * 4;
                        // BGRA：按亮度判断是否落在涂抹区（白色 = 重绘）。
                        var gray = (maskBuffer[maskIndex + 2] * 299 +
                                    maskBuffer[maskIndex + 1] * 587 +
                                    maskBuffer[maskIndex] * 114) / 1000;
                        outputBuffer[outputIndex + 3] = gray > 108 ? (byte)1 : (byte)255;
                    }
                }

                Marshal.Copy(outputBuffer, 0, outputData.Scan0, outputBuffer.Length);
            }
            finally
            {
                output.UnlockBits(outputData);
                maskScaled.UnlockBits(maskData);
            }

            using var result = new MemoryStream();
            output.Save(result, ImageFormat.Png);
            return result.ToArray();
        }

        private static string TruncateForMessage(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "服务返回空响应";
            value = value.Trim();
            return value.Length <= 300 ? value : value.Substring(0, 300);
        }

        private string ReadComfyUrl()
        {
            const string fallback = "http://127.0.0.1:8188";
            try
            {
                var path = Path.Combine(_dataRoot, "config.json");
                if (!File.Exists(path)) return fallback;
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("comfy_url", out var value) &&
                    value.ValueKind == JsonValueKind.String)
                {
                    var url = (value.GetString() ?? string.Empty).Trim().TrimEnd('/');
                    if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                        return url;
                }
            }
            catch
            {
                // Fall back to the bundled default when the config is unreadable.
            }
            return fallback;
        }

        private static bool TryExtractLoraNames(JsonElement root, string nodeClass, out List<string> names)
        {
            names = new List<string>();
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty(nodeClass, out var node) ||
                !node.TryGetProperty("input", out var input))
                return false;

            var found = false;
            foreach (var sectionName in new[] { "required", "optional" })
            {
                if (!input.TryGetProperty(sectionName, out var section) ||
                    section.ValueKind != JsonValueKind.Object)
                    continue;

                var bestList = default(JsonElement);
                var bestRank = int.MaxValue;
                foreach (var field in section.EnumerateObject())
                {
                    var rank = LoraInputRank(field.Name);
                    if (rank >= bestRank || field.Value.ValueKind != JsonValueKind.Array ||
                        field.Value.GetArrayLength() < 1)
                        continue;
                    var list = field.Value[0];
                    if (list.ValueKind != JsonValueKind.Array) continue;
                    bestList = list;
                    bestRank = rank;
                }

                if (bestRank == int.MaxValue) continue;
                found = true;
                foreach (var item in bestList.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String) continue;
                    var name = item.GetString();
                    // "None" is a loader-specific empty slot, not a LoRA file.
                    if (string.IsNullOrWhiteSpace(name) ||
                        name.Equals("None", StringComparison.Ordinal) ||
                        names.Contains(name, StringComparer.Ordinal))
                        continue;
                    names.Add(name);
                }
                break;
            }
            return found;
        }

        private static int LoraInputRank(string name)
        {
            if (name.Equals("lora_name", StringComparison.OrdinalIgnoreCase)) return 0;
            if (name.Equals("LoRA1名称", StringComparison.Ordinal)) return 1;
            if (name.StartsWith("LoRA", StringComparison.Ordinal) &&
                name.EndsWith("名称", StringComparison.Ordinal)) return 2;
            if (name.StartsWith("lora", StringComparison.OrdinalIgnoreCase) &&
                name.EndsWith("_name", StringComparison.OrdinalIgnoreCase)) return 3;
            return int.MaxValue;
        }

        private static bool IsApiWorkflow(string data)
        {
            try
            {
                using var document = JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 96 });
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.EnumerateObject().Any()) return false;

                var count = 0;
                foreach (var node in root.EnumerateObject())
                {
                    if (++count > 5000 || node.Value.ValueKind != JsonValueKind.Object ||
                        !node.Value.TryGetProperty("class_type", out var classType) ||
                        classType.ValueKind != JsonValueKind.String ||
                        string.IsNullOrWhiteSpace(classType.GetString()) ||
                        !node.Value.TryGetProperty("inputs", out var inputs) ||
                        inputs.ValueKind != JsonValueKind.Object)
                    {
                        return false;
                    }
                }
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static bool IsWorkflowFileId(string id)
        {
            return !string.IsNullOrWhiteSpace(id) && id.Length <= 240 &&
                   string.Equals(id, Path.GetFileName(id), StringComparison.Ordinal) &&
                   id.EndsWith(".api.json", StringComparison.OrdinalIgnoreCase) &&
                   !id.Any(char.IsControl) && id.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
        }

        private static async Task<byte[]> ReadBoundedBodyAsync(HttpContext context, int maxBytes, string tooLargeMessage)
        {
            if (context.Request.ContentLength.GetValueOrDefault() > maxBytes)
                throw new InvalidDataException(tooLargeMessage);

            using var output = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await context.Request.Body.ReadAsync(
                       buffer, 0, buffer.Length, context.RequestAborted).ConfigureAwait(false)) > 0)
            {
                if (output.Length + read > maxBytes)
                    throw new InvalidDataException(tooLargeMessage);
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }

        private static async Task CopyResponseAsync(HttpContext context, HttpResponseMessage response)
        {
            context.Response.StatusCode = (int)response.StatusCode;
            if (response.Content.Headers.ContentType != null)
                context.Response.ContentType = response.Content.Headers.ContentType.ToString();
            await response.Content.CopyToAsync(context.Response.Body).ConfigureAwait(false);
        }

        private static async Task WriteJsonAsync(HttpContext context, int statusCode, object body)
        {
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(JsonSerializer.Serialize(body), context.RequestAborted).ConfigureAwait(false);
        }

        private async Task<bool> IsAvailableAsync()
        {
            var client = _client;
            if (client == null) return false;
            try
            {
                using var timeout = new CancellationTokenSource(ProbeTimeout);
                // /api/info only reports console settings and remains available
                // when the ComfyUI backend has not started yet.
                using var response = await client.GetAsync("/api/info", timeout.Token).ConfigureAwait(false);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        private async Task<string> PreparePayloadAsync()
        {
            Directory.CreateDirectory(_dataRoot);
            var exe = Path.Combine(_dataRoot, "ComfyUI手机控制台.exe");
            if (!File.Exists(exe) || !BundledAssetEquals("ComfyUI手机控制台.exe", exe))
                CopyBundledAsset("ComfyUI手机控制台.exe", exe);

            var config = Path.Combine(_dataRoot, "config.json");
            if (!File.Exists(config)) CopyBundledAsset("config.json", config);

            var workflows = Path.Combine(_dataRoot, "api_workflows");
            Directory.CreateDirectory(workflows);
            foreach (var relativePath in EnumerateBundledWorkflowAssets())
            {
                var destination = Path.Combine(workflows, Path.GetFileName(relativePath));
                // Bundled workflow assets are shipped data: refresh stale copies so
                // parameter and GG option fixes reach existing installs. User-imported
                // workflows keep their own names and are never enumerated here.
                if (!File.Exists(destination) || !BundledAssetEquals(relativePath, destination))
                    CopyBundledAsset(relativePath, destination);
            }

            // 在随包资源刷新之后、标题/GG 同步之前重转本机工作流，保证后两者
            // 作用于最终产物。转换失败只记日志，不阻断通知和手机控制台启动。
            await TryAutoConvertWorkflowsAsync(workflows).ConfigureAwait(false);

            // 随包资源刷新会覆盖 .ui.json，用户改过的名称在这里重新写回。
            ApplyWorkflowTitleOverrides();

            // GG 选项确认节点是 ComfyUI 前端虚拟节点，不会出现在 API 图中。
            // 启动前解析随包的 .workflow.json，把该节点连接的目标控件自动补进
            // .ui.json 的字段白名单，并为缺失的后端控制器补一个 no-op 节点。
            ApplyGgOptionWorkflowSync(workflows);
            return exe;
        }

        private sealed class AutoConvertSettings
        {
            public bool Enabled { get; set; } = true;
            public string? Source { get; set; }
            public string? Python { get; set; }
            public string? BaseUrl { get; set; }
        }

        private AutoConvertSettings LoadAutoConvertSettings()
        {
            var path = Path.Combine(_dataRoot, AutoConvertConfigName);
            if (!File.Exists(path)) return new AutoConvertSettings();
            try
            {
                var settings = JsonSerializer.Deserialize<AutoConvertSettings>(
                    File.ReadAllText(path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return settings ?? new AutoConvertSettings();
            }
            catch (Exception error)
            {
                _log("ComfyUI 工作流自动重转：配置读取失败，" + error.Message);
                return new AutoConvertSettings { Enabled = false };
            }
        }

        private async Task TryAutoConvertWorkflowsAsync(string workflowsDirectory)
        {
            var settings = LoadAutoConvertSettings();
            if (!settings.Enabled) return;

            var baseUrl = ResolveConverterBaseUrl(settings.BaseUrl);
            var reachable = await IsEndpointReachableAsync(
                baseUrl, "/system_stats", TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            if (!reachable)
            {
                _log("ComfyUI 工作流自动重转：ComfyUI 未运行（" + baseUrl + "），跳过");
                return;
            }

            // 优先使用显式配置或常见安装位置；否则让正在运行的 ComfyUI 报告
            // 自己的目录，避免把某台机器的绝对路径硬编码进程序。
            var source = ResolveWorkflowSource(settings.Source);
            if (source == null)
                source = await ResolveSourceFromComfyUiAsync(baseUrl).ConfigureAwait(false);

            var python = ResolveConverterPython(settings.Python, source);
            if (python == null)
            {
                _log("ComfyUI 工作流自动重转：未找到 Python，跳过（可在 auto-convert.json 配置 python）");
                return;
            }
            if (source == null) source = DeriveSourceFromPython(python);
            if (source == null)
            {
                _log("ComfyUI 工作流自动重转：未找到 ComfyUI 用户工作流目录，跳过（可在 auto-convert.json 配置 source）");
                return;
            }

            string entry;
            try
            {
                entry = EnsureConverterScripts();
            }
            catch (Exception error)
            {
                _log("ComfyUI 工作流自动重转：内置转换脚本不可用，" + error.Message);
                return;
            }

            try
            {
                _log("ComfyUI 工作流自动重转：" + source);
                var exitCode = await RunConverterAsync(
                    python, entry, source, baseUrl, workflowsDirectory).ConfigureAwait(false);
                if (exitCode != 0)
                    _log("ComfyUI 工作流自动重转：转换脚本退出码 " + exitCode + "，已保留可用的旧工作流");
            }
            catch (Exception error)
            {
                _log("ComfyUI 工作流自动重转失败：" + error.Message);
            }
        }

        private static string? ResolveWorkflowSource(string? configured)
        {
            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(configured)) candidates.Add(configured.Trim());
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(profile))
            {
                candidates.Add(Path.Combine(profile, "ComfyUI", "user", "default", "workflows"));
                candidates.Add(Path.Combine(profile, "Documents", "ComfyUI", "user", "default", "workflows"));
            }
            candidates.Add(@"C:\ComfyUI\user\default\workflows");
            foreach (var candidate in candidates)
            {
                try
                {
                    if (Directory.Exists(candidate)) return Path.GetFullPath(candidate);
                }
                catch
                {
                    // Ignore malformed configured paths and keep probing defaults.
                }
            }
            return null;
        }

        private static async Task<string?> ResolveSourceFromComfyUiAsync(string baseUrl)
        {
            try
            {
                using var handler = new HttpClientHandler { UseProxy = false };
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
                var json = await client.GetStringAsync(baseUrl + "/internal/folder_paths")
                    .ConfigureAwait(false);
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind != JsonValueKind.Object) return null;

                // custom_nodes / models 等目录都在 ComfyUI 根目录下，向上找即可
                // 得到 user\default\workflows，不依赖安装目录的具体名称。
                foreach (var propertyName in new[] { "custom_nodes", "checkpoints", "loras" })
                {
                    if (!document.RootElement.TryGetProperty(propertyName, out var value) ||
                        value.ValueKind != JsonValueKind.Array)
                        continue;
                    foreach (var entry in value.EnumerateArray())
                    {
                        if (entry.ValueKind != JsonValueKind.String) continue;
                        var reported = entry.GetString();
                        if (string.IsNullOrWhiteSpace(reported)) continue;
                        var source = TryDeriveWorkflowSource(reported);
                        if (source != null) return source;
                    }
                }
            }
            catch
            {
                // Older ComfyUI builds may not expose internal routes.
            }
            return null;
        }

        private static string? TryDeriveWorkflowSource(string reportedPath)
        {
            try
            {
                var directory = new DirectoryInfo(reportedPath);
                if (!directory.Exists) directory = directory.Parent;
                for (var depth = 0; depth < 5 && directory != null;
                     depth++, directory = directory.Parent)
                {
                    var candidate = Path.Combine(directory.FullName, "user", "default", "workflows");
                    if (Directory.Exists(candidate)) return candidate;
                }
            }
            catch
            {
                // Ignore paths reported by a remote or removed ComfyUI install.
            }
            return null;
        }

        private static string? DeriveSourceFromPython(string python)
        {
            try
            {
                var directory = Path.GetDirectoryName(python);
                if (string.IsNullOrWhiteSpace(directory)) return null;
                // ComfyUI 便携版布局：<root>\python_embeded\python.exe 与
                // <root>\ComfyUI\user\default\workflows 并列。
                if (!string.Equals(Path.GetFileName(directory), "python_embeded",
                        StringComparison.OrdinalIgnoreCase))
                    return null;
                var root = Path.GetDirectoryName(directory);
                if (string.IsNullOrWhiteSpace(root)) return null;
                var candidate = Path.Combine(root, "ComfyUI", "user", "default", "workflows");
                return Directory.Exists(candidate) ? candidate : null;
            }
            catch
            {
                return null;
            }
        }

        private static string? ResolveConverterPython(string? configured, string? source)
        {
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
                return configured;

            var fromEnvironment = Environment.GetEnvironmentVariable("TAPRELAY_PYTHON");
            if (!string.IsNullOrWhiteSpace(fromEnvironment) && File.Exists(fromEnvironment))
                return fromEnvironment;

            if (!string.IsNullOrWhiteSpace(source))
            {
                try
                {
                    var directory = new DirectoryInfo(source);
                    while (directory != null)
                    {
                        var embedded = Path.Combine(directory.FullName, "python_embeded", "python.exe");
                        if (File.Exists(embedded)) return embedded;
                        directory = directory.Parent;
                    }
                }
                catch
                {
                    // Fall through to PATH probing.
                }
            }

            foreach (var name in new[] { "python.exe", "python3.exe", "py.exe" })
            {
                var onPath = FindOnPath(name);
                if (onPath != null) return onPath;
            }
            return null;
        }

        private static string? FindOnPath(string fileName)
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var raw in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                try
                {
                    var candidate = Path.Combine(raw.Trim().Trim('"'), fileName);
                    if (File.Exists(candidate)) return candidate;
                }
                catch
                {
                    // Skip invalid PATH entries.
                }
            }
            return null;
        }

        private string ResolveConverterBaseUrl(string? configured)
        {
            var candidate = (configured ?? string.Empty).Trim();
            if (candidate.Length == 0) candidate = ReadComfyUrl();
            candidate = candidate.TrimEnd('/');
            return Uri.TryCreate(candidate, UriKind.Absolute, out var uri) &&
                   (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                ? candidate
                : "http://127.0.0.1:8188";
        }

        private static async Task<bool> IsEndpointReachableAsync(
            string baseUrl, string path, TimeSpan timeout)
        {
            try
            {
                using var handler = new HttpClientHandler { UseProxy = false };
                using var client = new HttpClient(handler) { Timeout = timeout };
                using var response = await client
                    .GetAsync(baseUrl + path, HttpCompletionOption.ResponseHeadersRead)
                    .ConfigureAwait(false);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        private string EnsureConverterScripts()
        {
            var toolsDirectory = Path.Combine(_dataRoot, "tools");
            Directory.CreateDirectory(toolsDirectory);
            foreach (var name in new[] { ConverterEntryName, ConverterModuleName })
            {
                var relativePath = "tools\\" + name;
                var destination = Path.Combine(toolsDirectory, name);
                if (!File.Exists(destination) || !BundledAssetEquals(relativePath, destination))
                    CopyBundledAsset(relativePath, destination);
            }
            return Path.Combine(toolsDirectory, ConverterEntryName);
        }

        private async Task<int> RunConverterAsync(
            string python, string entry, string source, string baseUrl, string workflowsDirectory)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = python,
                WorkingDirectory = Path.GetDirectoryName(entry) ?? _dataRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
            startInfo.Environment["PYTHONUTF8"] = "1";
            startInfo.Environment["PYTHONUNBUFFERED"] = "1";
            // 不在数据目录留下 __pycache__。
            startInfo.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
            if (string.Equals(Path.GetFileName(python), "py.exe", StringComparison.OrdinalIgnoreCase))
                startInfo.ArgumentList.Add("-3");
            startInfo.ArgumentList.Add(entry);
            startInfo.ArgumentList.Add("--apply");
            startInfo.ArgumentList.Add("--out");
            startInfo.ArgumentList.Add(workflowsDirectory);
            startInfo.ArgumentList.Add("--base-url");
            startInfo.ArgumentList.Add(baseUrl);
            startInfo.ArgumentList.Add("--source");
            startInfo.ArgumentList.Add(source);

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
                throw new InvalidOperationException("无法启动工作流转换脚本");
            TrackConverterProcess(process);
            try
            {
                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();
                var exited = await Task.Run(
                    () => process.WaitForExit((int)AutoConvertTimeout.TotalMilliseconds))
                    .ConfigureAwait(false);
                if (!exited)
                {
                    try { process.Kill(true); } catch { }
                    _log("ComfyUI 工作流自动重转：超过 " + (int)AutoConvertTimeout.TotalSeconds
                         + " 秒未完成，已终止");
                    return -1;
                }
                var stdout = await stdoutTask.ConfigureAwait(false);
                var stderr = await stderrTask.ConfigureAwait(false);
                LogConverterSummary(stdout, stderr);
                return process.ExitCode;
            }
            finally
            {
                UntrackConverterProcess(process);
            }
        }

        private void LogConverterSummary(string stdout, string stderr)
        {
            var summary = new List<string>();
            foreach (var raw in (stdout ?? string.Empty).Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("候选工作流", StringComparison.Ordinal) ||
                    line.StartsWith("写入 ", StringComparison.Ordinal) ||
                    line.StartsWith("可直接运行", StringComparison.Ordinal) ||
                    line.StartsWith("诊断合计", StringComparison.Ordinal) ||
                    line.StartsWith("未解析输入归因", StringComparison.Ordinal) ||
                    line.StartsWith("插件/节点未加载", StringComparison.Ordinal) ||
                    line.StartsWith("源工作流未连线", StringComparison.Ordinal) ||
                    line.StartsWith("旁路节点", StringComparison.Ordinal) ||
                    line.StartsWith("链接表断链", StringComparison.Ordinal))
                    summary.Add(line);
            }
            if (!string.IsNullOrWhiteSpace(stderr))
                summary.Add("错误：" + TruncateForMessage(stderr));
            _log(summary.Count == 0
                ? "ComfyUI 工作流自动重转：转换脚本未输出统计"
                : "ComfyUI 工作流自动重转：" + string.Join("；", summary));
        }

        private void TrackConverterProcess(Process process)
        {
            lock (_converterGate) _converterProcess = process;
        }

        private void UntrackConverterProcess(Process process)
        {
            lock (_converterGate)
            {
                if (ReferenceEquals(_converterProcess, process)) _converterProcess = null;
            }
        }

        private void KillConverterProcess()
        {
            Process? converter;
            lock (_converterGate)
            {
                converter = _converterProcess;
                _converterProcess = null;
            }
            if (converter == null) return;
            try
            {
                if (!converter.HasExited) converter.Kill(true);
            }
            catch
            {
                // The converter may have exited between the check and the kill.
            }
        }

        private static IEnumerable<string> EnumerateBundledWorkflowAssets()
        {
            var sourceDirectory = Path.Combine(
                AppContext.BaseDirectory, "Assets", "ComfyUIConsole", "api_workflows");
            if (Directory.Exists(sourceDirectory))
            {
                foreach (var file in Directory.GetFiles(sourceDirectory, "*", SearchOption.TopDirectoryOnly))
                    yield return "api_workflows\\" + Path.GetFileName(file);
            }

            const string marker = ".Assets.ComfyUIConsole.";
            foreach (var resourceName in typeof(ComfyUiConsoleHost).Assembly.GetManifestResourceNames())
            {
                var markerIndex = resourceName.IndexOf(marker, StringComparison.Ordinal);
                if (markerIndex < 0) continue;
                var relative = resourceName.Substring(markerIndex + marker.Length);
                if (relative.StartsWith("api_workflows.", StringComparison.Ordinal))
                    yield return "api_workflows\\" + relative.Substring("api_workflows.".Length);
            }
        }

        private static Stream OpenBundledAsset(string relativePath)
        {
            var sourcePath = Path.Combine(
                AppContext.BaseDirectory, "Assets", "ComfyUIConsole", relativePath);
            if (File.Exists(sourcePath)) return File.OpenRead(sourcePath);

            var resourceName = typeof(ComfyUiConsoleHost).Assembly.GetName().Name +
                               ".Assets.ComfyUIConsole." +
                               relativePath.Replace('\\', '.').Replace('/', '.');
            return typeof(ComfyUiConsoleHost).Assembly.GetManifestResourceStream(resourceName)
                   ?? throw new FileNotFoundException("未找到内置 ComfyUI 资源", relativePath);
        }

        private static bool BundledAssetEquals(string relativePath, string destination)
        {
            using var source = OpenBundledAsset(relativePath);
            using var target = File.OpenRead(destination);
            using var sha = SHA256.Create();
            var sourceHash = sha.ComputeHash(source);
            var targetHash = sha.ComputeHash(target);
            return sourceHash.SequenceEqual(targetHash);
        }

        private static void CopyBundledAsset(string relativePath, string destination)
        {
            var directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            using var source = OpenBundledAsset(relativePath);
            using var target = File.Create(destination);
            source.CopyTo(target);
        }

        private static readonly JsonSerializerOptions WorkflowAssetJsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        private sealed class GgOption
        {
            public GgOption(string key, string label, string targetNode, string targetType, string input)
            {
                Key = key;
                Label = label;
                TargetNode = targetNode;
                TargetType = targetType;
                Input = input;
            }

            public string Key { get; }
            public string Label { get; }
            public string TargetNode { get; }
            public string TargetType { get; }
            public string Input { get; }
        }

        private sealed class GgControllerGroup
        {
            public GgControllerGroup(string optionKey, IReadOnlyList<string> mutedNodes)
            {
                OptionKey = optionKey;
                MutedNodes = mutedNodes;
            }

            public string OptionKey { get; }
            public IReadOnlyList<string> MutedNodes { get; }
        }

        private void ApplyGgOptionWorkflowSync(string workflowsDirectory)
        {
            var controllerGroups =
                new Dictionary<string, List<GgControllerGroup>>(StringComparer.Ordinal);
            foreach (var workflowPath in Directory.GetFiles(
                         workflowsDirectory, "*.workflow.json", SearchOption.TopDirectoryOnly))
            {
                var fileName = Path.GetFileName(workflowPath);
                var baseName = fileName.Substring(0, fileName.Length - ".workflow.json".Length);
                var apiPath = Path.Combine(workflowsDirectory, baseName + ".api.json");
                var uiPath = Path.Combine(workflowsDirectory, baseName + ".ui.json");
                if (!File.Exists(apiPath) || !File.Exists(uiPath)) continue;

                try
                {
                    var options = ReadGgOptions(workflowPath);
                    if (options.Count == 0) continue;
                    ApplyGgOptionsToUi(uiPath, options);
                    EnsureGgControllerNodes(apiPath, options);
                    var groups = ReadGgControllerGroups(workflowPath, options);
                    _log("GG 选项同步：" + baseName + "，选项 " + options.Count +
                         "，控制器编组 " + groups.Count);
                    if (groups.Count > 0)
                        controllerGroups[Path.GetFileName(apiPath)] = groups;
                }
                catch (Exception error)
                {
                    // 单个工作流解析失败不应阻止控制台启动，内置 .ui.json 仍然可用。
                    _log("自动同步 GG 选项失败（" + baseName + "）：" + error.Message);
                }
            }
            _controllerGroups = controllerGroups;
            // 启动时确认控制器编组已解析出来：这条日志是排查“开关关闭后仍返回对比图”的
            // 第一现场，能看到工作流键、选项键和被隐藏的节点 ID。
            foreach (var pair in controllerGroups)
            {
                foreach (var group in pair.Value)
                {
                    _log("GG 单组控制：" + pair.Key + " / " + group.OptionKey +
                         " → 隐藏 " + string.Join(",", group.MutedNodes));
                }
            }
        }

        private static List<GgOption> ReadGgOptions(string workflowPath)
        {
            var result = new List<GgOption>();
            using var document = JsonDocument.Parse(
                File.ReadAllText(workflowPath),
                new JsonDocumentOptions { MaxDepth = 128 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("nodes", out var nodes) ||
                nodes.ValueKind != JsonValueKind.Array)
                return result;

            var nodeById = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var node in nodes.EnumerateArray())
            {
                var id = ReadNodeId(node);
                if (!string.IsNullOrEmpty(id)) nodeById[id!] = node;
            }

            var linkTargets = new Dictionary<long, string>();
            if (root.TryGetProperty("links", out var links) && links.ValueKind == JsonValueKind.Array)
            {
                foreach (var link in links.EnumerateArray())
                {
                    if (link.ValueKind != JsonValueKind.Array || link.GetArrayLength() < 5) continue;
                    if (!TryReadLong(link[0], out var linkId)) continue;
                    var targetId = ReadNodeIdValue(link[3]);
                    if (!string.IsNullOrEmpty(targetId)) linkTargets[linkId] = targetId!;
                }
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in nodes.EnumerateArray())
            {
                if (node.ValueKind != JsonValueKind.Object) continue;
                if (!string.Equals(ReadString(node, "type"), "GGOptionConfirm",
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!node.TryGetProperty("outputs", out var outputs) ||
                    outputs.ValueKind != JsonValueKind.Array)
                    continue;

                var slotNames = ReadSlotNames(node);
                var slot = 0;
                foreach (var output in outputs.EnumerateArray())
                {
                    var currentSlot = slot++;
                    if (output.ValueKind != JsonValueKind.Object) continue;
                    if (!output.TryGetProperty("links", out var outputLinks) ||
                        outputLinks.ValueKind != JsonValueKind.Array ||
                        outputLinks.GetArrayLength() == 0)
                        continue;

                    string? targetId = null;
                    foreach (var linkIdElement in outputLinks.EnumerateArray())
                    {
                        if (TryReadLong(linkIdElement, out var linkId) &&
                            linkTargets.TryGetValue(linkId, out var found))
                        {
                            targetId = found;
                            break;
                        }
                    }
                    if (targetId == null || !nodeById.TryGetValue(targetId, out var targetNode))
                        continue;

                    var input = ReadWidgetName(output);
                    if (string.IsNullOrWhiteSpace(input)) continue;
                    var key = targetId + "." + input;
                    if (!seen.Add(key)) continue;

                    var label = currentSlot < slotNames.Count &&
                                !string.IsNullOrWhiteSpace(slotNames[currentSlot])
                        ? slotNames[currentSlot]
                        : ReadString(output, "name") ?? input;
                    result.Add(new GgOption(
                        key, label, targetId, ReadString(targetNode, "type") ?? "", input));
                }
            }

            return result;
        }

        private static List<GgControllerGroup> ReadGgControllerGroups(
            string workflowPath, List<GgOption> options)
        {
            var result = new List<GgControllerGroup>();
            using var document = JsonDocument.Parse(
                File.ReadAllText(workflowPath),
                new JsonDocumentOptions { MaxDepth = 128 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("nodes", out var nodes) ||
                nodes.ValueKind != JsonValueKind.Array)
                return result;

            var boundsByGroup = new Dictionary<string, double[]>(StringComparer.Ordinal);
            if (root.TryGetProperty("groups", out var groups) &&
                groups.ValueKind == JsonValueKind.Array)
            {
                foreach (var group in groups.EnumerateArray())
                {
                    if (group.ValueKind != JsonValueKind.Object) continue;
                    var title = ReadString(group, "title");
                    if (string.IsNullOrWhiteSpace(title)) continue;
                    if (!group.TryGetProperty("bounding", out var bounding) ||
                        bounding.ValueKind != JsonValueKind.Array ||
                        bounding.GetArrayLength() < 4)
                        continue;

                    var values = new double[4];
                    var valid = true;
                    for (var index = 0; index < 4; index++)
                    {
                        if (!bounding[index].TryGetDouble(out values[index]))
                        {
                            valid = false;
                            break;
                        }
                    }
                    if (valid) boundsByGroup[title!] = values;
                }
            }

            foreach (var controller in nodes.EnumerateArray())
            {
                if (controller.ValueKind != JsonValueKind.Object ||
                    !string.Equals(ReadString(controller, "type"), "GGGroupControllerS",
                        StringComparison.OrdinalIgnoreCase))
                    continue;

                var controllerId = ReadNodeId(controller);
                if (string.IsNullOrEmpty(controllerId)) continue;
                if (!controller.TryGetProperty("properties", out var properties) ||
                    properties.ValueKind != JsonValueKind.Object)
                    continue;

                var groupName = ReadString(properties, "selectedGroup");
                if (string.IsNullOrWhiteSpace(groupName) ||
                    !boundsByGroup.TryGetValue(groupName!, out var bounds))
                    continue;

                var option = options.FirstOrDefault(item =>
                    string.Equals(item.TargetNode, controllerId, StringComparison.Ordinal));
                if (option == null) continue;

                var muted = new List<string>();
                foreach (var node in nodes.EnumerateArray())
                {
                    var nodeId = ReadNodeId(node);
                    if (string.IsNullOrEmpty(nodeId) || nodeId == controllerId) continue;
                    if (!node.TryGetProperty("pos", out var pos) ||
                        pos.ValueKind != JsonValueKind.Array ||
                        pos.GetArrayLength() < 2)
                        continue;
                    if (!pos[0].TryGetDouble(out var x) || !pos[1].TryGetDouble(out var y))
                        continue;
                    if (x < bounds[0] || y < bounds[1] ||
                        x > bounds[0] + bounds[2] || y > bounds[1] + bounds[3])
                        continue;
                    if (!muted.Contains(nodeId!)) muted.Add(nodeId!);
                }

                if (muted.Count > 0)
                    result.Add(new GgControllerGroup(option.Key, muted));
            }

            return result;
        }

        private static string? ReadNodeId(JsonElement node)
        {
            return node.ValueKind == JsonValueKind.Object &&
                   node.TryGetProperty("id", out var id)
                ? ReadNodeIdValue(id)
                : null;
        }

        private static string? ReadNodeIdValue(JsonElement id)
        {
            switch (id.ValueKind)
            {
                case JsonValueKind.String:
                    return id.GetString();
                case JsonValueKind.Number:
                    return id.GetRawText();
                default:
                    return null;
            }
        }

        private static string? ReadString(JsonElement element, string propertyName)
        {
            return element.ValueKind == JsonValueKind.Object &&
                   element.TryGetProperty(propertyName, out var value) &&
                   value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }

        private static bool TryReadLong(JsonElement element, out long value)
        {
            value = 0L;
            if (element.ValueKind == JsonValueKind.Number)
                return element.TryGetInt64(out value);
            if (element.ValueKind == JsonValueKind.String)
                return long.TryParse(element.GetString(), out value);
            return false;
        }

        private static List<string> ReadSlotNames(JsonElement node)
        {
            var result = new List<string>();
            if (node.ValueKind != JsonValueKind.Object ||
                !node.TryGetProperty("properties", out var properties) ||
                properties.ValueKind != JsonValueKind.Object ||
                !properties.TryGetProperty("gg_option_slot_names", out var names) ||
                names.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var name in names.EnumerateArray())
                result.Add(name.ValueKind == JsonValueKind.String ? name.GetString() ?? "" : "");
            return result;
        }

        private static string? ReadWidgetName(JsonElement output)
        {
            if (output.ValueKind == JsonValueKind.Object &&
                output.TryGetProperty("widget", out var widget) &&
                widget.ValueKind == JsonValueKind.Object)
            {
                var name = ReadString(widget, "name");
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
            return ReadString(output, "name");
        }

        private static void ApplyGgOptionsToUi(string uiPath, List<GgOption> options)
        {
            var root = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                File.ReadAllText(uiPath));
            if (root == null) return;

            var fields = new List<string>();
            if (root.TryGetValue("fields", out var fieldsElement) &&
                fieldsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var field in fieldsElement.EnumerateArray())
                {
                    var value = field.ValueKind == JsonValueKind.String ? field.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(value)) fields.Add(value!);
                }
            }

            var labels = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root.TryGetValue("labels", out var labelsElement) &&
                labelsElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var label in labelsElement.EnumerateObject())
                    labels[label.Name] = label.Value.ValueKind == JsonValueKind.String
                        ? label.Value.GetString() ?? ""
                        : "";
            }

            var ggKeys = new HashSet<string>(StringComparer.Ordinal);
            var ordered = new List<string>();
            foreach (var option in options)
            {
                if (!ggKeys.Add(option.Key)) continue;
                ordered.Add(option.Key);
                labels[option.Key] = option.Label;
            }
            foreach (var field in fields)
            {
                if (!ggKeys.Contains(field) && !ordered.Contains(field))
                    ordered.Add(field);
            }

            var output = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var pair in root)
            {
                if (pair.Key == "fields" || pair.Key == "labels") continue;
                output[pair.Key] = pair.Value;
            }
            output["fields"] = ordered;
            output["labels"] = labels;
            File.WriteAllText(uiPath, JsonSerializer.Serialize(output, WorkflowAssetJsonOptions));
        }

        private static void EnsureGgControllerNodes(string apiPath, List<GgOption> options)
        {
            var additions = new Dictionary<string, GgOption>(StringComparer.Ordinal);
            foreach (var option in options)
            {
                if (!string.Equals(option.TargetType, "GGGroupControllerS",
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!additions.ContainsKey(option.TargetNode))
                    additions[option.TargetNode] = option;
            }
            if (additions.Count == 0) return;

            var root = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                File.ReadAllText(apiPath));
            if (root == null) return;

            var changed = false;
            foreach (var addition in additions)
            {
                if (root.ContainsKey(addition.Key)) continue;
                var node = new Dictionary<string, object>
                {
                    ["inputs"] = new Dictionary<string, object> { [addition.Value.Input] = true },
                    ["class_type"] = addition.Value.TargetType,
                    ["_meta"] = new Dictionary<string, object> { ["title"] = "GG 单组控制" }
                };
                using var document = JsonDocument.Parse(JsonSerializer.Serialize(node));
                root[addition.Key] = document.RootElement.Clone();
                changed = true;
            }
            if (changed)
                File.WriteAllText(apiPath, JsonSerializer.Serialize(root, WorkflowAssetJsonOptions));
        }

        private void WritePrivateConfig(int port)
        {
            var path = Path.Combine(_dataRoot, "config.json");
            var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (File.Exists(path))
            {
                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(path));
                    if (document.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var property in document.RootElement.EnumerateObject())
                            values[property.Name] = property.Value.Clone();
                    }
                }
                catch
                {
                    values.Clear();
                }
            }

            if (!values.ContainsKey("comfy_url"))
                values["comfy_url"] = ParseJsonElement("\"http://127.0.0.1:8188\"");
            values["port"] = ParseJsonElement(port.ToString());
            values["bind"] = ParseJsonElement("\"127.0.0.1\"");
            values["open_admin"] = ParseJsonElement("false");
            File.WriteAllText(path, JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true }));
        }

        private static JsonElement ParseJsonElement(string json)
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }

        private async Task StopProcessAsync()
        {
            // 自动重转是外部 Python 进程，停止服务或退出程序时必须一并清理。
            KillConverterProcess();

            var process = _process;
            _process = null;
            _client?.Dispose();
            _client = null;

            if (_jobHandle != IntPtr.Zero)
            {
                CloseHandle(_jobHandle);
                _jobHandle = IntPtr.Zero;
            }
            if (process != null)
            {
                try
                {
                    if (!process.HasExited) await Task.Run(() => process.WaitForExit(1500)).ConfigureAwait(false);
                }
                catch { }
                finally { process.Dispose(); }
            }
        }

        private static int FindAvailableLoopbackPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static bool IsLoopback(IPAddress? address)
        {
            if (address == null) return false;
            return IPAddress.IsLoopback(address) ||
                   (address.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(address.MapToIPv4()));
        }

        private void AttachKillOnParentExitJob(Process process)
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) throw new InvalidOperationException("无法创建 ComfyUI 进程生命周期边界");

            var info = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = KillOnJobClose
                }
            };
            var size = Marshal.SizeOf(typeof(JobObjectExtendedLimitInformation));
            var memory = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, memory, false);
                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, memory, (uint)size) ||
                    !AssignProcessToJobObject(job, process.Handle))
                {
                    throw new InvalidOperationException("无法绑定 ComfyUI 控制台进程生命周期");
                }
                _jobHandle = job;
            }
            catch
            {
                CloseHandle(job);
                try { if (!process.HasExited) process.Kill(); } catch { }
                throw;
            }
            finally
            {
                Marshal.FreeHGlobal(memory);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectExtendedLimitInformation
        {
            public JobObjectBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr jobAttributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(
            IntPtr job,
            int informationClass,
            IntPtr information,
            uint informationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
