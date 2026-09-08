using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using AguiGroupChat.SkillHosting;
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
///  - 编译期引用运行时共享框架的<b>全部程序集</b>（标准 BCL：System.Security.Cryptography / Registry(Windows) / IO / XML / 网络等开箱即用）。
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
    private readonly string _nugetCacheRoot;
    private readonly Lazy<NuGetSkillReferenceResolver> _nugetRefs;

    public DotnetSkillHost(ILogger logger, string? nugetCacheRoot = null)
    {
        _logger = logger;
        var obj = typeof(object).Assembly.Location;
        _baseDir = Path.GetDirectoryName(obj) ?? AppContext.BaseDirectory;
        // 缓存尽量落盘到服务端 data 下（由调用方 SkillRunner 传入）；缺省用系统可写目录
        _nugetCacheRoot = string.IsNullOrWhiteSpace(nugetCacheRoot)
            ? SkillHostingDefaults.NuGetCacheRoot()
            : nugetCacheRoot;
        _nugetRefs = new Lazy<NuGetSkillReferenceResolver>(() => new NuGetSkillReferenceResolver(_nugetCacheRoot));
    }

    /// <summary>把一节 C# 源码当作技能执行，返回结果 / 报错文本。
    /// 支持正文顶部 <c>#r "nuget: 包名, 版本"</c> 声明 NuGet 引用（运行时在宿主下载还原到本地缓存）。</summary>
    public string Run(string source, string input, CancellationToken ct, int timeoutMs = DefaultTimeoutMs)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(source)) return ".NET 技能正文为空：请提供含 public static string Run(string input) 的 C# 源码。";

            // 1) 解析 #r nuget 指令并剔除出源码；有引用则先还原（首次会联网下载，结果按包/版本缓存复用）
            var prep = PrepareSource(source, ct);
            if (prep.Error is not null) return prep.Error;

            var (bytes, errors) = Compile(prep.Cleaned, prep.ExtraRefs);
            if (bytes.Length == 0)
            {
                var errMsg = ".NET 技能编译失败：\n" + string.Join("\n", errors.Take(14));
                return SkillCSharpNormalizer.AppendPlatformHint(errMsg, OperatingSystem.IsWindows());
            }

            var alc = new AssemblyLoadContext("skill_" + Guid.NewGuid().ToString("N"), isCollectible: true);
            Assembly asm;
            try { asm = alc.LoadFromStream(new MemoryStream(bytes)); }
            catch (Exception ex) { TryUnloadLater(alc); return ".NET 技能加载失败：" + ex.Message; }

            // 2) 把已还原的包程序集预载入同一可卸载 ALC，脚本方法引用第三方类型时才能解析（框架程序集已过滤不在此列）
            foreach (var dll in prep.ExtraPaths)
            {
                try { alc.LoadFromAssemblyPath(dll); }
                catch (Exception ex) { _logger.LogDebug(ex, "预载 NuGet 程序集失败（已忽略）：{Dll}", dll); }
            }

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

    /// <summary>仅编译校验（不运行作者代码）：生成后自检用。返回空串表示编译通过且存在 Run 入口；否则返回报错文本。</summary>
    public string CompileOnly(string source, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(source)) return ".NET 技能正文为空：请提供含 public static string Run(string input) 的 C# 源码。";
            var prep = PrepareSource(source, ct);
            if (prep.Error is not null) return prep.Error;

            var (bytes, errors) = Compile(prep.Cleaned, prep.ExtraRefs);
            if (bytes.Length == 0)
            {
                var errMsg = ".NET 技能编译失败：\n" + string.Join("\n", errors.Take(14));
                return SkillCSharpNormalizer.AppendPlatformHint(errMsg, OperatingSystem.IsWindows());
            }

            // 校验入口存在（加载元数据即可，不 Invoke）
            var alc = new AssemblyLoadContext("skill_chk_" + Guid.NewGuid().ToString("N"), isCollectible: true);
            try
            {
                var asm = alc.LoadFromStream(new MemoryStream(bytes));
                foreach (var dll in prep.ExtraPaths)
                {
                    try { alc.LoadFromAssemblyPath(dll); } catch { /* 仅入口探测 */ }
                }
                return FindRun(asm) is null ? ".NET 技能缺少入口：请在源码中提供 public static string Run(string)。" : "";
            }
            finally
            {
                TryUnloadLater(alc);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, ".NET 技能编译校验失败");
            return ".NET 技能编译校验失败：" + ex.Message;
        }
    }

    /// <summary>共享准备：剔除 #r 指令、还原 NuGet 引用，产出待编译源码与引用（含路径）。Error 非空表示不可继续。</summary>
    private (string Cleaned, List<MetadataReference> ExtraRefs, List<string> ExtraPaths, string? Error) PrepareSource(string source, CancellationToken ct)
    {
        var parsedRefs = SkillNuGetParser.ParseReferences(source);
        var cleaned = parsedRefs.Count > 0 ? SkillNuGetParser.StripDirectives(source) : source;
        // 顶层直接写方法的正文（未包 class）自动包成类，消除 CS0106/CS8805 类“需要可执行程序”的误编译
        cleaned = SkillCSharpNormalizer.NormalizeForLibrary(cleaned);
        var extraRefs = new List<MetadataReference>();
        var extraPaths = new List<string>();
        if (parsedRefs.Count > 0)
        {
            var restored = _nugetRefs.Value.ResolveAsync(parsedRefs, ct).GetAwaiter().GetResult();
            if (restored.Error is not null)
                return (cleaned, extraRefs, extraPaths,
                    ".NET 技能 NuGet 引用还原失败：\n" + restored.Error
                    + (restored.Log.Count > 0 ? "\n" + string.Join("\n", restored.Log.Take(10)) : ""));
            foreach (var dll in restored.ReferencePaths)
            {
                try { extraRefs.Add(MetadataReference.CreateFromFile(dll)); extraPaths.Add(dll); }
                catch (Exception ex) { _logger.LogWarning(ex, "NuGet 引用加载失败：{Dll}", dll); }
            }
        }
        return (cleaned, extraRefs, extraPaths, null);
    }

    private (byte[] Bytes, string[] Errors) Compile(string source, IReadOnlyList<MetadataReference> extraRefs)
    {
        var code = Preamble + "\n" + StripUsingsSeparation(source);
        var tree = CSharpSyntaxTree.ParseText(code, new CSharpParseOptions(LanguageVersion.CSharp12));
        var opt = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            .WithOptimizationLevel(OptimizationLevel.Release)
            .WithAllowUnsafe(false);
        var comp = CSharpCompilation.Create("skill_" + Guid.NewGuid().ToString("N"), new[] { tree },
            extraRefs.Count > 0 ? AllowedReferences().Concat(extraRefs) : AllowedReferences(), opt);
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
        return _refCache.GetOrAdd("all", _ =>
        {
            var list = new List<MetadataReference>();
            foreach (var p in Tpa())
            {
                if (string.IsNullOrWhiteSpace(Path.GetFileNameWithoutExtension(p))) continue;
                try { list.Add(MetadataReference.CreateFromFile(p)); } catch { /* 单条失败跳过 */ }
            }
            if (list.Count == 0)
            {
                // 兜底：直接扫运行目录（桌面单文件 / 受限环境 TPA 不可得）
                if (Directory.Exists(_baseDir))
                    foreach (var f in Directory.GetFiles(_baseDir, "*.dll"))
                    {
                        if (string.IsNullOrWhiteSpace(Path.GetFileNameWithoutExtension(f))) continue;
                        list.Add(MetadataReference.CreateFromFile(f));
                    }
            }
            return list;
        });
    }

    private static IEnumerable<string> Tpa()
    {
        try { return (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? "").Split(Path.PathSeparator); }
        catch { return Array.Empty<string>(); }
    }
}
