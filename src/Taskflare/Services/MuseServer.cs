using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Taskflare.Models;

namespace Taskflare.Services;

/// <summary>
/// The AI connector: a small web server inside Taskflare that lets AI assistants (Claude, ChatGPT, Gemini, Grok,
/// Meta's Muse, Siri through Apple Shortcuts…) add and read tasks. It speaks two languages:
///   • MCP (Model Context Protocol, streamable HTTP) at  /mcp/&lt;key&gt;  — what custom connectors use
///   • a plain JSON REST API at                         /api/&lt;key&gt;/…  — for agents that build their own connector
/// Add ?via=claude (chatgpt, gemini, grok, muse, siri…) to the address so tasks are labelled with the assistant that added them.
/// It listens on this PC only (localhost). The assistants run in the cloud, so <see cref="TunnelService"/> can give it a
/// public https address through a Cloudflare quick tunnel. Every request needs the secret key.
/// </summary>
public static class MuseServer
{
    private static WebApplication _app;

    public static bool IsRunning { get; private set; }
    public static int Port { get; private set; }
    public static string Error { get; private set; }
    public static int RequestCount { get; private set; }
    public static DateTime? LastRequest { get; private set; }

    public static event Action StatusChanged;

    public static string LocalBase => $"http://localhost:{Port}";

    /// <summary>MCP session id → the assistant that opened it (from its clientInfo name).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Sessions = new();

    private static readonly string[] ProtocolVersions = { "2025-06-18", "2025-03-26", "2024-11-05" };

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    public static async Task StartAsync(int port)
    {
        await StopAsync();
        Error = null;
        try
        {
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            {
                ContentRootPath = AppContext.BaseDirectory,
                EnvironmentName = "Production",
            });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(o =>
            {
                o.ListenLocalhost(port);
                o.Limits.MaxRequestBodySize = 2 * 1024 * 1024;
                o.AddServerHeader = false;
            });
            var app = builder.Build();
            app.Run(Handle);
            await app.StartAsync();
            _app = app;
            Port = port;
            IsRunning = true;
        }
        catch (Exception ex)
        {
            IsRunning = false;
            Error = ex is IOException || ex.InnerException is System.Net.Sockets.SocketException || ex.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
                ? $"Port {port} is already in use by another program. Pick a different port."
                : ex.Message;
            _app = null;
        }
        StatusChanged?.Invoke();
    }

    public static async Task StopAsync()
    {
        var app = _app;
        _app = null;
        IsRunning = false;
        if (app != null)
        {
            try { using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2)); await app.StopAsync(cts.Token); } catch { }
            try { await app.DisposeAsync(); } catch { }
        }
        StatusChanged?.Invoke();
    }

    // ---------- request handling ----------

    private static async Task Handle(HttpContext ctx)
    {
        var path = (ctx.Request.Path.Value ?? "/").TrimEnd('/');
        var seg = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var method = ctx.Request.Method.ToUpperInvariant();

        if (method == "OPTIONS")
        {
            ctx.Response.Headers["Allow"] = "GET, POST, PATCH, DELETE, OPTIONS";
            ctx.Response.StatusCode = 204;
            return;
        }

        if (seg.Length == 0)
        {
            await Text(ctx, 200, "text/html; charset=utf-8",
                "<!doctype html><title>Taskflare</title><body style=\"font-family:Segoe UI,sans-serif;padding:2em\">" +
                "<h2>Taskflare connector is running</h2><p>This is the connector that lets AI assistants add tasks to Taskflare on this PC. " +
                "The address with your secret key is in Taskflare &rsaquo; Settings &rsaquo; AI assistants.</p></body>");
            return;
        }
        if (seg[0].Equals("health", StringComparison.OrdinalIgnoreCase))
        {
            await Json(ctx, 200, new JsonObject { ["app"] = "Taskflare", ["version"] = AppInfo.Version, ["ok"] = true });
            return;
        }

        var kind = seg[0].ToLowerInvariant();
        if (kind is not ("mcp" or "api"))
        {
            await Json(ctx, 404, new JsonObject { ["error"] = "Not found. Use /mcp/<key> or /api/<key>." });
            return;
        }

        // The key can be in the path (/mcp/<key>), a Bearer token, an X-Api-Key header or ?key=
        var key = SettingsService.Current.MuseKey;
        string[] rest;
        if (seg.Length > 1 && KeyMatches(seg[1], key)) rest = seg[2..];
        else if (KeyMatches(HeaderKey(ctx.Request), key)) rest = seg[1..];
        else
        {
            ctx.Response.Headers["WWW-Authenticate"] = "Bearer";
            await Json(ctx, 401, new JsonObject { ["error"] = "Missing or wrong key. Copy the connector address from Taskflare › Settings › AI assistants." });
            return;
        }

        RequestCount++;
        LastRequest = DateTime.Now;
        try { System.Windows.Application.Current?.Dispatcher.InvokeAsync(() => StatusChanged?.Invoke()); } catch { }

        var via = ctx.Request.Query["via"].ToString();
        if (kind == "mcp" && rest.Length > 0) via = rest[0]; // /mcp/<key>/claude
        if (string.IsNullOrEmpty(via)) via = ctx.Request.Headers["X-Taskflare-Via"].ToString();
        if (kind == "mcp") await HandleMcp(ctx, method, string.IsNullOrEmpty(via) ? null : AiNames.Normalize(via));
        else await HandleApi(ctx, method, rest, string.IsNullOrEmpty(via) ? "ai" : AiNames.Normalize(via));
    }

    private static string HeaderKey(HttpRequest r)
    {
        var auth = r.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return auth[7..].Trim();
        var x = r.Headers["X-Api-Key"].ToString();
        if (!string.IsNullOrEmpty(x)) return x.Trim();
        return r.Query["key"].ToString();
    }

    private static bool KeyMatches(string given, string key)
    {
        if (string.IsNullOrEmpty(given) || string.IsNullOrEmpty(key)) return false;
        var a = Encoding.UTF8.GetBytes(given);
        var b = Encoding.UTF8.GetBytes(key);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    // ---------- MCP (JSON-RPC 2.0 over streamable HTTP; answers are plain JSON) ----------

    private static async Task HandleMcp(HttpContext ctx, string method, string via)
    {
        if (method == "GET")
        {
            // No server-to-client stream; the spec allows 405 here.
            ctx.Response.Headers["Allow"] = "POST";
            ctx.Response.StatusCode = 405;
            return;
        }
        if (method == "DELETE")
        {
            Sessions.TryRemove(ctx.Request.Headers["Mcp-Session-Id"].ToString(), out _);
            ctx.Response.StatusCode = 200;
            return;
        }
        if (method != "POST")
        {
            ctx.Response.StatusCode = 405;
            return;
        }

        JsonNode body;
        try
        {
            using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
            body = JsonNode.Parse(await reader.ReadToEndAsync());
        }
        catch
        {
            await Json(ctx, 400, RpcError(null, -32700, "Parse error"));
            return;
        }

        var session = ctx.Request.Headers["Mcp-Session-Id"].ToString();
        string source = via ?? (Sessions.TryGetValue(session, out var known) ? known : "ai");
        if (body is JsonObject one && StrOf(one["method"]) == "initialize")
        {
            // A new session: remember who it is, from the client's name, unless the link says ?via=
            session = Guid.NewGuid().ToString("N");
            var client = StrOf(one["params"]?["clientInfo"]?["name"]);
            source = via ?? AiNames.Guess(client);
            Sessions[session] = source;
            if (Sessions.Count > 500) Sessions.Clear();
        }
        if (!string.IsNullOrEmpty(session)) ctx.Response.Headers["Mcp-Session-Id"] = session;

        if (body is JsonArray batch)
        {
            var answers = new JsonArray();
            foreach (var item in batch)
            {
                var a = await HandleRpc(item as JsonObject, source);
                if (a != null) answers.Add(a);
            }
            if (answers.Count == 0) { ctx.Response.StatusCode = 202; return; }
            await Json(ctx, 200, answers);
            return;
        }

        var answer = await HandleRpc(body as JsonObject, source);
        if (answer == null) { ctx.Response.StatusCode = 202; return; }
        await Json(ctx, 200, answer);
    }

    /// <summary>Returns null for notifications (no id).</summary>
    private static async Task<JsonNode> HandleRpc(JsonObject req, string source)
    {
        if (req == null) return RpcError(null, -32600, "Invalid request");
        var id = req["id"];
        var method = req["method"]?.GetValue<string>() ?? "";
        if (id == null) return null; // notification, e.g. notifications/initialized

        var p = req["params"] as JsonObject ?? new JsonObject();
        try
        {
            switch (method)
            {
                case "initialize":
                {
                    var asked = (p["protocolVersion"] as JsonValue)?.GetValue<string>();
                    var version = ProtocolVersions.Contains(asked) ? asked : ProtocolVersions[0];
                    return Ok(id, new JsonObject
                    {
                        ["protocolVersion"] = version,
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                        ["serverInfo"] = new JsonObject { ["name"] = "taskflare", ["title"] = "Taskflare", ["version"] = AppInfo.Version },
                        ["instructions"] = MuseTools.Instructions,
                    });
                }
                case "ping":
                    return Ok(id, new JsonObject());
                case "tools/list":
                    return Ok(id, new JsonObject { ["tools"] = JsonNode.Parse(MuseTools.ToolsJson) });
                case "tools/call":
                {
                    var name = (p["name"] as JsonValue)?.GetValue<string>() ?? "";
                    var args = p["arguments"] as JsonObject;
                    var (result, error) = await RunTool(name, args?.DeepClone() as JsonObject, source);
                    var text = error ?? result.ToJsonString(Pretty);
                    return Ok(id, new JsonObject
                    {
                        ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } },
                        ["isError"] = error != null,
                    });
                }
                case "resources/list":
                    return Ok(id, new JsonObject { ["resources"] = new JsonArray() });
                case "prompts/list":
                    return Ok(id, new JsonObject { ["prompts"] = new JsonArray() });
                default:
                    return RpcError(id, -32601, $"Method not found: {method}");
            }
        }
        catch (Exception ex)
        {
            return RpcError(id, -32603, ex.Message);
        }
    }

    private static string StrOf(JsonNode n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static JsonObject Ok(JsonNode id, JsonNode result) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result };

    private static JsonObject RpcError(JsonNode id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    /// <summary>Runs a tool on the UI thread. Returns (result, null) or (null, error message).</summary>
    public static async Task<(JsonNode result, string error)> RunTool(string name, JsonObject args, string source = "ai")
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null) return (null, "Taskflare is closing.");
        try
        {
            var r = await dispatcher.InvokeAsync(() => MuseTools.Execute(name, args, source));
            return (r, null);
        }
        catch (ToolError e)
        {
            return (null, e.Message);
        }
        catch (Exception e)
        {
            return (null, "Taskflare couldn't do that: " + e.Message);
        }
    }

    // ---------- REST ----------

    private static async Task HandleApi(HttpContext ctx, string method, string[] rest, string source)
    {
        var r = rest.Select(s => s.ToLowerInvariant()).ToArray();
        JsonObject body = null;
        if (method is "POST" or "PATCH" or "PUT")
        {
            try
            {
                using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
                var text = await reader.ReadToEndAsync();
                var node = string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text);
                body = node as JsonObject ?? (node is JsonArray arr ? new JsonObject { ["tasks"] = arr.DeepClone() } : new JsonObject());
            }
            catch
            {
                await Json(ctx, 400, new JsonObject { ["error"] = "The body must be JSON." });
                return;
            }
        }

        string tool = null;
        var args = body ?? new JsonObject();
        var q = ctx.Request.Query;

        if (r.Length == 0 && method == "GET")
        {
            await Text(ctx, 200, "text/plain; charset=utf-8", ApiDocs(ctx));
            return;
        }
        if (r.Length == 1 && r[0] == "lists")
        {
            if (method == "GET") tool = "list_lists";
            else if (method == "POST") tool = "create_list";
        }
        else if (r.Length == 1 && r[0] == "tasks")
        {
            if (method == "GET")
            {
                tool = "list_tasks";
                foreach (var k in new[] { "view", "list", "search", "limit" })
                    if (q.ContainsKey(k)) args[k] = q[k].ToString();
            }
            else if (method == "POST") tool = args.ContainsKey("tasks") ? "add_tasks" : "add_task";
        }
        else if (r.Length == 1 && (r[0] == "quick-add" || r[0] == "quickadd") && method == "POST") tool = "quick_add";
        else if (r.Length >= 2 && r[0] == "tasks")
        {
            args["id"] = rest[1];
            if (r.Length == 3 && r[2] == "complete" && method == "POST") tool = "complete_task";
            else if (r.Length == 3 && r[2] == "uncomplete" && method == "POST") { tool = "complete_task"; args["completed"] = false; }
            else if (r.Length == 2 && method is "PATCH" or "PUT" or "POST") tool = "update_task";
            else if (r.Length == 2 && method == "DELETE") tool = "delete_task";
            else if (r.Length == 2 && method == "GET")
            {
                var (found, err) = await RunGet(rest[1]);
                if (found == null) await Json(ctx, 404, new JsonObject { ["error"] = err });
                else await Json(ctx, 200, found);
                return;
            }
        }

        if (tool == null)
        {
            await Json(ctx, 404, new JsonObject { ["error"] = "Unknown endpoint. GET /api/<key> lists them." });
            return;
        }
        var (result, error) = await RunTool(tool, args, source);
        if (error != null) await Json(ctx, error.StartsWith("No task", StringComparison.Ordinal) ? 404 : 400, new JsonObject { ["error"] = error });
        else await Json(ctx, method == "POST" && tool.StartsWith("add", StringComparison.Ordinal) ? 201 : 200, result);
    }

    private static async Task<(JsonNode, string)> RunGet(string id)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null) return (null, "Taskflare is closing.");
        return await dispatcher.InvokeAsync<(JsonNode, string)>(() =>
        {
            var t = TaskStore.Find(id);
            return t == null ? (null, $"No task with id '{id}'.") : (MuseTools.TaskJson(t), null);
        });
    }

    private static string ApiDocs(HttpContext ctx)
    {
        var host = ctx.Request.Host.Value;
        var scheme = ctx.Request.Headers["X-Forwarded-Proto"].ToString();
        if (string.IsNullOrEmpty(scheme)) scheme = host.Contains("trycloudflare", StringComparison.OrdinalIgnoreCase) ? "https" : ctx.Request.Scheme;
        var b = $"{scheme}://{host}{ctx.Request.PathBase}{ctx.Request.Path.Value?.TrimEnd('/')}";
        return $$"""
        Taskflare API — the user's to-do list on their Windows PC.
        All requests and responses are JSON. The key is already part of this address.

        {{MuseTools.Instructions}}

        GET    {{b}}/tasks                     List open tasks. Query: view=all|my_day|important|planned|overdue|today|completed|muse, list=<name>, search=<text>, limit=<n>
        POST   {{b}}/tasks                     Add a task:  {"title": "Book dentist", "list": "Personal", "due_date": "2026-11-20", "due_time": "14:00", "priority": "high", "important": true, "add_to_my_day": true, "notes": "...", "steps": ["..."], "repeat": "weekly", "reminder": "2026-11-20T13:30"}
                                             Several at once: {"tasks": [{"title": "Milk"}, {"title": "Eggs"}], "list": "Shopping"}
        POST   {{b}}/quick-add                 {"text": "Call mom tomorrow 5pm #Personal p1"}
        GET    {{b}}/tasks/<id>                One task
        PATCH  {{b}}/tasks/<id>                Change fields (same names as POST; "" clears a date)
        POST   {{b}}/tasks/<id>/complete       Tick it off   (…/uncomplete to undo)
        DELETE {{b}}/tasks/<id>                Delete it
        GET    {{b}}/lists                     Lists and their open-task counts
        POST   {{b}}/lists                     {"name": "Holiday packing", "color": "#22C55E"}

        MCP clients can use {{b.Replace("/api/", "/mcp/")}} instead (streamable HTTP).
        Add ?via=claude (or chatgpt, gemini, grok, muse, siri…) to any address so the tasks you add are labelled with your name.
        """;
    }

    private static async Task Json(HttpContext ctx, int status, JsonNode node)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsync(node.ToJsonString(Pretty));
    }

    private static async Task Text(HttpContext ctx, int status, string type, string text)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = type;
        await ctx.Response.WriteAsync(text);
    }
}
