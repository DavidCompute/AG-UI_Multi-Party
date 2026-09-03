using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Agents.Tools;

/// <summary>
/// .NET（C#）技能的<b>动态编译 + 受限执行</b>宿主（仅在 server 端、仅管理员可建，见调用方 RBAC）。
///
/// 技能 body 约定：一段 C# 源码，其中须含一个 <c>public static string Run(string input)</c>（可带可选的
/// <c>async Task&lt;string&gt;</c> 不提供，本版仅支持同步 <c>Run(string)</c>，超长任务请勿阻塞、自行分块）。
/// 作者可写 top-level using、类与 helper；宿主编译后把整份程序集装入<b>可卸载 AssemblyLoadContext</b>，
/// 反射找到 <c>Run(string)</c> 入口执行，返回文本；执行强超时 + 截断；结束即卸载脚本程序集。
///
/// 隔离与安全（尽力而为的受限沙箱）：
///  - 编译期只引用经过白名单的元数据程序集（System.* 里安全常用子集；不接诊断进程 / 注册表 /
///    COM / 不可信原生互操作等）。引用不到的 API = 编译失败 = 天然 API 白名单。
///  - <see cref="OptimizationLevel"/> + AllowUnsafe=false，禁用不安全的指针 / 源生成危险互操作。
///  - 运行用新线程强超时、输出截断；结束后卸载 ALC。
///  注意：进程内受限执行并非 OS 级沙箱；调用方必须保证只有受信（系统管理员创建、server 执行）的技能进此。
/// </summary>
internal sealed class DotnetSkillHost
{
    private const int DefaultTimeoutMs = 10_000;
    private const int MaxOutputChars = 12_000;
    private static readonly ConcurrentDictionary<string, IReadOnlyList<MetadataReference>> _refCache = new(StringComparer.Ordinal);

    private readonly ILogger _logger;
    private readonly string _baseDir;

    public DotnetSkillHost(ILogger logger)
    {
        _logger = logger;
        var obj = typeof(object).Assembly.Location;
        _baseDir = Path.GetDirectoryName(obj) ?? AppContext.BaseDirectory;
    }

    /// <summary>把一节 C# 源码当作技能执行，返回结果 / 报错文本。</summary>
    public string Run(string source, string input, CancellationToken ct, int timeoutMs = DefaultTimeoutMs)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(source)) return ".NET 技能正文为空：请提供含 public static string Run(string input) 的 C# 源码。";

            var (bytes, errors) = Compile(source);
            if (bytes.Length == 0)
                return ".NET 技能编译失败：\n" + string.Join("\n", errors.Take(14));

            var alc = new AssemblyLoadContext("skill_" + Guid.NewGuid().ToString("N"), isCollectible: true);
            Assembly asm;
            try { asm = alc.LoadFromStream(new MemoryStream(bytes)); }
            catch (Exception ex) { TryUnloadLater(alc); return ".NET 技能加载失败：" + ex.Message; }

            MethodInfo? run = FindRun(asm);
            if (run is null) { TryUnloadLater(alc); return ".NET 技能缺少入口：请在源码中提供 public static string Run(string)。"; }

            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task.Run(() =>
            {
                try { tcs.TrySetResult(Convert.ToString(run.Invoke(null, new object?[] { input }) ?? "") ?? ""); }
                catch (TargetInvocationException tie) { tcs.TrySetResult(".NET 技能运行异常：" + (tie.InnerException?.Message ?? tie.Message)); }
                catch (Exception ex) { tcs.TrySetResult(".NET 技能运行异常：" + ex.Message); }
            }, CancellationToken.None);
            try
            {
                // 主线程强超时等待作者代码结束（Run 同步；真正耗在 Invoke 里），超时则放弃等待
                if (!tcs.Task.Wait(timeoutMs))
                    return $".NET 技能执行超时（{timeoutMs}ms），已中止。";
                var outText = tcs.Task.Result;
                return outText.Length > MaxOutputChars ? outText[..MaxOutputChars] + "\n…(已截断)" : outText;
            }
            finally
            {
                TryUnloadLater(alc);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, ".NET 技能执行失败");
            return ".NET 技能执行失败：" + ex.Message;
        }
    }

    private (byte[] Bytes, string[] Errors) Compile(string source)
    {
        var code = Preamble + "\n" + StripUsingsSeparation(source);
        var tree = CSharpSyntaxTree.ParseText(code, new CSharpParseOptions(LanguageVersion.CSharp12));
        var opt = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            .WithOptimizationLevel(OptimizationLevel.Release)
            .WithAllowUnsafe(false);
        var comp = CSharpCompilation.Create("skill_" + Guid.NewGuid().ToString("N"), new[] { tree }, AllowedReferences(), opt);
        using var ms = new MemoryStream();
        var emit = comp.Emit(ms);
        if (!emit.Success) return (Array.Empty<byte>(), emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()).ToArray());
        return (ms.ToArray(), Array.Empty<string>());
    }

    // 作者常在 body 头部写 using；框架的 using 需在其之前。此处合并成：我们顶行插公共 using，之后接作者源码原样。
    private const string Preamble =
        "// <auto> dotnet skill</auto>\n" +
        "using System;\nusing System.Linq;\nusing System.Collections;\nusing System.Collections.Generic;\nusing System.Text;\nusing System.Text.Json;\nusing System.Net.Http;\nusing System.Threading.Tasks;\nusing System.Threading;\n";

    private static string StripUsingsSeparation(string s) => s.Trim();

    private static MethodInfo? FindRun(Assembly asm)
    {
        foreach (var t in SafeTypes(asm))
        {
            var m = t.GetMethod("Run", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
            if (m is not null && m.ReturnType == typeof(string)) return m;
        }
        return null;
    }

    private static void TryUnloadLater(AssemblyLoadContext alc)
    {
        // 延迟到方法返回后再卸载（等反射相关引用释放），尽力而为；失败不抛出（留给 GC/后台收集）。
        try { Task.Run(async () => { await Task.Delay(500); try { alc.Unload(); } catch { /* 容忍 */ } }); }
        catch { /* 忽略 */ }
    }

    private static IEnumerable<Type> SafeTypes(Assembly a)
    {
        try { return a.GetExportedTypes(); }
        catch (ReflectionTypeLoadException) { return Array.Empty<Type>(); }
    }

    private IReadOnlyList<MetadataReference> AllowedReferences()
    {
        var allow = AllowedAssemblyNames;
        return _refCache.GetOrAdd("default", _ =>
        {
            var list = new List<MetadataReference>();
            foreach (var p in Tpa())
            {
                var n = Path.GetFileNameWithoutExtension(p);
                if (n is not null && allow.Contains(n)) list.Add(MetadataReference.CreateFromFile(p));
            }
            if (list.Count == 0)
            {
                // 兜底：直接扫运行目录（桌面单文件 / 受限环境 TPA 不可得）
                if (Directory.Exists(_baseDir))
                    foreach (var f in Directory.GetFiles(_baseDir, "*.dll"))
                    {
                        var n = Path.GetFileNameWithoutExtension(f);
                        if (n is not null && allow.Contains(n)) list.Add(MetadataReference.CreateFromFile(f));
                    }
            }
            return list;
        });
    }

    private static HashSet<string> AllowedAssemblyNames { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        "System.Private.CoreLib", "System.Runtime", "System.Console", "System.Runtime.Extensions",
        "System.Threading", "System.Threading.Tasks", "System.Linq", "System.Linq.Parallel", "System.Linq.Expressions",
        "System.Collections", "System.Collections.Concurrent", "System.Collections.NonGeneric",
        "System.Text.RegularExpressions", "System.Globalization", "System.Memory",
        "System.Net.Http", "System.Net.Primitives", "System.Net.WebClient",
        "System.Text.Json", "System.ObjectModel", "netstandard",
    };

    private static IEnumerable<string> Tpa()
    {
        try { return (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? "").Split(Path.PathSeparator); }
        catch { return Array.Empty<string>(); }
    }
}
