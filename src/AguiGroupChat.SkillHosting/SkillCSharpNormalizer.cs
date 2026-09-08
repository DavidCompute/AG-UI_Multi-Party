using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AguiGroupChat.SkillHosting;

/// <summary>
/// C# 技能正文的小型规范化：模型/用户常把方法直接写在文件顶层（没包进 class）。
/// Roslyn 会把这种“顶层方法”解析成 <see cref="GlobalStatementSyntax"/> + 局部函数，
/// 按库编译时产生 CS0106「修饰符 public 无效」/ CS8805「顶层语句须为可执行程序」。
/// 这类正文在编译前自动包进一个 class，使「public static string Run(string) 在类里」的约定成立。
/// 真正含可执行顶层语句的 Program 式写法不强行包（编译报错交由自动修复器重写成类）。
/// </summary>
public static class SkillCSharpNormalizer
{
    /// <summary>返回可在“库 + 反射入口”宿主下编译的源码：检测到“整份正文都是顶层方法/字段式成员”时自动包类。</summary>
    public static string NormalizeForLibrary(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) return source ?? "";

        SyntaxTree tree;
        try { tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp12)); }
        catch { return source; }

        var cu = (CompilationUnitSyntax)tree.GetRoot();
        if (cu.Members.Count == 0) return source;
        // 已含类型 / 命名空间声明（class/record/struct/interface/enum/namespace）：作者已正确组织，原样返回
        if (cu.Members.Any(m => m is BaseTypeDeclarationSyntax or BaseNamespaceDeclarationSyntax)) return source;

        // 顶层“方法声明”在语法树上实际是 GlobalStatement + 局部函数；正文若全部由这类成员构成即可安全包类。
        // 只要出现真正的可执行顶层语句（表达式 / 变量赋值 / 顶层变量等）就不包——那不是方法式正文。
        bool allWrapable = cu.Members.All(m =>
            m is MethodDeclarationSyntax                                                       // 理论分支（正常语法树不会出现）
            or GlobalStatementSyntax { Statement: LocalFunctionStatementSyntax });
        if (!allWrapable) return source;

        // 包进一个合成类（保留其前置 using / 注释在文件级）。类内的“public 局部函数”会按类方法解析。
        var first = cu.Members[0];
        var insertAt = Math.Max(0, first.SpanStart);
        var before = source[..insertAt];
        var body = source[insertAt..];
        return before + "public class Skill\n{\n" + body + "\n}\n";
    }

    /// <summary>平台专属 API（注册表 / WMI 等）在非 Windows 宿主编译必然失败——给出“改到 Windows 本机(client)执行”的提示。</summary>
    public static string AppendPlatformHint(string message, bool hostIsWindows)
    {
        if (hostIsWindows) return message;
        if (message.Contains("Microsoft.Win32", StringComparison.Ordinal)
            || message.Contains("Registry", StringComparison.Ordinal)
            || message.Contains("System.Management", StringComparison.Ordinal))
        {
            return message + "\n提示：当前编译宿主不是 Windows，注册表/WMI 等 API 仅存在于 Windows；"
                + "如需读取/写入注册表等本机资源，请把该技能的 executionLocation 改为 client（在本机 Windows 经本机桥执行）后再试。";
        }
        return message;
    }
}
