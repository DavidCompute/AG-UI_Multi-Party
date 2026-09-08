using System.Reflection;
using System.Runtime.Loader;
using AguiGroupChat.SkillHosting;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace AguiGroupChat.NativeBridge;

/// <summary>
/// 本机（Client/桥）dotnet 技能的动态编译 + 受限执行宿主：
/// 把收到的 C# 源码（含 public static string Run(string input)）用 Roslyn 编译为内存程序集，
/// 装入可卸载的 <see cref="AssemblyLoadContext"/>，反射执行并返回文本；强超时 + 截断 + 结束卸载。
/// 正文顶部可用 <c>#r "nuget: 包名, 版本"</c> 声明 NuGet 引用——运行时在本机下载还原到本地缓存
/// （缓存目录见 <see cref="SkillHostingDefaults"/>），还原出的包程序集既参与编译也预载入运行 ALC。
/// 安全边界同服务端：编译引用运行时共享框架全量程序集（排除少量高危运维面）+ 显式声明的包、AllowUnsafe=false、超时/截断；
/// 桥运行在用户本机，信任面与本机 shell 同级（由“仅管理员可建 dotnet 技能”+审批兜底）。
/// </summary>
internal sealed class DotnetRunner
{
    private const int TimeoutMs = 10_000;
    private const int MaxOutputChars = 12_000;

    private readonly Lazy<NuGetSkillReferenceResolver> _nugetRefs = new(
        () => new NuGetSkillReferenceResolver(SkillHostingDefaults.NuGetCacheRoot()));

    public async Task<string> RunAsync(string source, string input, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(source)) return ".NET 技能正文为空。";

        // 解析 #r nuget 指令并剔除；顶层直接写方法的正文自动包成类（消除 CS0106/CS8805）；有引用先还原
        var parsedRefs = SkillNuGetParser.ParseReferences(source);
        var cleaned = parsedRefs.Count > 0 ? SkillNuGetParser.StripDirectives(source) : source;
        cleaned = SkillCSharpNormalizer.NormalizeForLibrary(cleaned);
        var extraPaths = new List<string>();
        if (parsedRefs.Count > 0)
        {
            var restored = await _nugetRefs.Value.ResolveAsync(parsedRefs, ct);
            if (restored.Error is not null)
                return ".NET 技能 NuGet 引用还原失败：\n" + restored.Error
                    + (restored.Log.Count > 0 ? "\n" + string.Join("\n", restored.Log.Take(10)) : "");
            extraPaths.AddRange(restored.ReferencePaths);
        }

        var paths = extraPaths;
        var result = await Task.Run(() => ExecuteSync(cleaned, input, paths), ct);
        return result ?? "";
    }

    private static string ExecuteSync(string source, string input, IReadOnlyList<string> extraPaths)
    {
        var refs = References();
        var extraRefs = new List<MetadataReference>(extraPaths.Count);
        foreach (var dll in extraPaths)
        {
            try { extraRefs.Add(MetadataReference.CreateFromFile(dll)); }
            catch { /* 单条失败不阻断编译（类型缺失时编译错误会提示） */ }
        }
        var tree = CSharpSyntaxTree.ParseText(Preamble + "\n" + source, new CSharpParseOptions(LanguageVersion.CSharp12));
        var opt = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary).WithOptimizationLevel(OptimizationLevel.Release).WithAllowUnsafe(false);
        var comp = CSharpCompilation.Create("b_" + Guid.NewGuid().ToString("N"), new[] { tree }, extraRefs.Count > 0 ? refs.Concat(extraRefs) : refs, opt);
        using var ms = new MemoryStream();
        var emit = comp.Emit(ms);
        if (!emit.Success)
            return ".NET 技能编译失败：\n" + string.Join("\n", emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Take(12).Select(d => d.ToString()));

        var alc = new AssemblyLoadContext("bridge_" + Guid.NewGuid().ToString("N"), isCollectible: true);
        try
        {
            var asm = alc.LoadFromStream(new MemoryStream(ms.ToArray()));
            // 已还原的包程序集预载入同一 ALC，脚本引用第三方类型时可解析（框架程序集已在还原时过滤）
            foreach (var dll in extraPaths)
            {
                try { alc.LoadFromAssemblyPath(dll); }
                catch { /* 忽略（如重复 / 非托管） */ }
            }
            var run = FindRun(asm);
            if (run is null) return ".NET 技能缺少入口：需含 public static string Run(string)。";
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var task = Task.Run(() =>
            {
                try { tcs.TrySetResult(Convert.ToString(run.Invoke(null, new object?[] { input }) ?? "") ?? ""); }
                catch (TargetInvocationException tie) { tcs.TrySetResult(".NET 技能运行异常：" + (tie.InnerException?.Message ?? tie.Message)); }
                catch (Exception ex) { tcs.TrySetResult(".NET 技能运行异常：" + ex.Message); }
            }, CancellationToken.None);
            if (!tcs.Task.Wait(TimeoutMs)) return $".NET 技能执行超时（{TimeoutMs}ms），已中止。";
            var outText = tcs.Task.Result;
            return outText.Length > MaxOutputChars ? outText[..MaxOutputChars] + "\n…(已截断)" : outText;
        }
        catch (Exception ex) { return ".NET 技能加载/调用失败：" + ex.Message; }
        finally { _ = Task.Run(async () => { await Task.Delay(400); try { alc.Unload(); } catch { } }); }
    }

    private const string Preamble =
        "// <auto> native dotnet skill</auto>\n" +
        "using System;\nusing System.Linq;\nusing System.Collections;\nusing System.Collections.Generic;\nusing System.Text;\nusing System.Text.Json;\nusing System.Net.Http;\nusing System.Threading;\nusing System.Threading.Tasks;\n";

    private static MethodInfo? FindRun(Assembly asm)
    {
        foreach (var t in SafeTypes(asm))
        {
            var m = t.GetMethod("Run", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
            if (m is not null && m.ReturnType == typeof(string)) return m;
        }
        return null;
    }

    private static IEnumerable<Type> SafeTypes(Assembly a)
    {
        try { return a.GetExportedTypes(); }
        catch (ReflectionTypeLoadException) { return Array.Empty<Type>(); }
    }

    private static List<MetadataReference> References()
    {
        var tpa = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var list = new List<MetadataReference>();
        foreach (var p in tpa)
        {
            if (string.IsNullOrWhiteSpace(Path.GetFileNameWithoutExtension(p))) continue;
            try { list.Add(MetadataReference.CreateFromFile(p)); } catch { }
        }
        return list;
    }
}
