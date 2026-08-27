using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Models;
using Microsoft.AspNetCore.Hosting;

namespace AguiGroupChat.Web;

/// <summary>
/// 客户端执行技能的本机桥 HTTP API：前端把「在客户端执行」的 shell 技能交到此端点执行（隔离工作目录 + 超时 + 输出截断）。
/// 复用 HITL 通道：先由 <see cref="AguiGroupChat.Agents.AgentGateway"/> 下发 <c>kind=client_tool</c> 交互卡，
/// 前端在自己的浏览器 / WebView 里点「在客户端执行」→ 本端点执行并回传 <c>toolResult</c> → 网关回灌模型继续。
/// 安全模型：仅登录用户可调用（<see cref="WebIdentity.RequireIdentityFilter"/>），命令在专属沙箱目录运行、超时受限。
/// HTTP 类客户端技能由前端直接用 <c>fetch</c> 执行（浏览器跨域 / 地址可达性由客户端自身决定），不经此桥。
/// </summary>
public static class ClientToolBridgeApi
{
    // 单次执行最大时长（秒）：与前端默认一致，超时后强制终止进程树
    private const int MaxTimeoutSec = 30;
    // 单次返回最大输出字符数：防止把超大命令输出塞进模型上下文 / 回灌消息
    private const int MaxOutputChars = 12_000;

    public static void MapClientToolBridgeApi(this WebApplication app)
    {
        var root = app.MapGroup("/ag-ui/client-tool");
        root.MapPost("/", async (ClientToolRunRequest req, HttpContext ctx, IWebHostEnvironment env, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("AguiGroupChat.Web.ClientToolBridge");
            // 客户端执行技能一律需审批（网关侧已强制 RequiresApproval=true），此处仅作本机桥执行；
            // 命令正文来自技能定义 / 前端回传，运行于隔离沙箱目录。
            if (string.IsNullOrWhiteSpace(req.Command))
                return Results.BadRequest(new { error = "缺少要执行的 shell 命令（command）" });

            // 以请求者 userId 为沙箱隔离维度，避免不同账号写同一目录
            var userId = WebIdentity.UserId(ctx);
            var rootDir = Path.Combine(env.ContentRootPath, "data", "clienttoolruns", SanitizeSegment(userId ?? "anonymous"));
            Directory.CreateDirectory(rootDir);

            string output;
            try
            {
                output = await RunShellAsync(rootDir, req.Command, req.Cwd, req.TimeoutSec, req.Query, ct);
                ClientToolTrace.Write($"BRIDGE-OK cmd={req.Command} outputLen={output.Length} outputHead={output.Substring(0, Math.Min(120, output.Length)).Replace(Environment.NewLine, " ")}");
            }
            catch (OperationCanceledException)
            {
                ClientToolTrace.Write($"BRIDGE-CANCEL cmd={req.Command}");
                return Results.Json(new { output = null as string, error = "客户端技能执行已取消（超时或连接中断）。" }, statusCode: StatusCodes.Status408RequestTimeout);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "客户端技能（shell）执行失败：{Cmd}", req.Command);
                ClientToolTrace.Write($"BRIDGE-ERR cmd={req.Command} error={ex.Message}");
                return Results.Json(new { output = null as string, error = "客户端技能执行失败：" + ex.Message }, statusCode: StatusCodes.Status500InternalServerError);
            }

            return Results.Ok(new { output });
        }).AddEndpointFilter(new WebIdentity.RequireIdentityFilter());
    }

    private static async Task<string> RunShellAsync(string rootDir, string command, string? cwd, int? timeoutSec, string? query, CancellationToken ct)
    {
        // 工作目录：默认沙箱根；允许相对子目录，但不允许逃逸到沙箱之外（路径穿越防御）
        var workDir = rootDir;
        if (!string.IsNullOrWhiteSpace(cwd) && cwd != ".")
        {
            var candidate = Path.GetFullPath(Path.Combine(rootDir, cwd));
            if (candidate.StartsWith(rootDir + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                workDir = candidate;
        }
        Directory.CreateDirectory(workDir);

        // 命令落盘为脚本执行（与技能 shell 执行同款思路）：Unix 下 bash 脚本；Windows 下 PowerShell(UUID 生成目录内)。
        string fileName, argsText;
        if (OperatingSystem.IsWindows())
        {
            // PowerShell -EncodedCommand：命令经 base64(UTF-16LE) 编码传递，规避 cmd 引号 / 中文代码页问题；
            // 前置强制控制台 UTF-8 输出，配合下方 UTF-8 解码保证中文结果不乱码。
            var ps = "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8;" + command;
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(ps));
            fileName = "powershell.exe";
            argsText = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded;
        }
        else
        {
            var scriptPath = Path.Combine(workDir, "client_run.sh");
            await File.WriteAllTextAsync(scriptPath, command, Encoding.UTF8, ct);
            fileName = "/bin/bash";
            argsText = "\"" + scriptPath + "\"";
        }
        var argvJson = JsonSerializer.Serialize(new { query = query ?? "" });

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = argsText,
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.Environment["QUERY"] = query ?? "";
        psi.Environment["ARGV_JSON"] = argvJson;

        var timeoutMs = Math.Clamp(timeoutSec.GetValueOrDefault(MaxTimeoutSec), 1, MaxTimeoutSec) * 1000;
        using var proc = new Process { StartInfo = psi };
        if (!proc.Start()) throw new InvalidOperationException("无法启动命令进程。");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        var completed = proc.WaitForExit(timeoutMs);
        if (!completed) proc.Kill(entireProcessTree: true);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (!completed) return "（客户端技能执行超时，已终止）";

        var sb = new StringBuilder();
        if (stdout.Length > 0) sb.AppendLine(stdout.TrimEnd());
        if (stderr.Length > 0) sb.AppendLine("stderr: " + stderr.TrimEnd());
        sb.AppendLine($"（退出码 {proc.ExitCode}）");
        return Truncate(sb.ToString().TrimEnd());
    }

    private static string SanitizeSegment(string s)
    {
        var chars = s.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.').ToArray();
        var v = new string(chars);
        return string.IsNullOrWhiteSpace(v) ? "anonymous" : v;
    }

    private static string Truncate(string? s)
        => string.IsNullOrWhiteSpace(s) ? "（命令无输出）" : (s.Length <= MaxOutputChars ? s : s[..MaxOutputChars] + "\n…（输出已截断）");
}

/// <summary>客户端（shell）工具本机桥执行请求。</summary>
public sealed record ClientToolRunRequest(
    string Kind,
    string? Command,
    string? Cwd,
    int? TimeoutSec,
    string? Query = null);
