using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 修改<b>进程级</b>环境变量（如 <c>AGUI_DOCX_OUT</c>）的测试必须串行执行。
///
/// <para>
/// 为什么需要：xUnit 默认按 collection 并行跑不同测试类，而 <see cref="System.Environment.SetEnvironmentVariable(string, string?)"/>
/// 影响的是<b>整个进程</b>——并行中的其它测试（尤其同样依赖该变量的技能测试）会读到别的用例设的值，
/// 表现为「单独跑全过、全量跑偶发失败」的假 flaky。实测踩到过。
/// </para>
///
/// <para>
/// 用法：把这类测试类标上 <c>[Collection(EnvVarCollection.Name)]</c>，
/// 它们彼此之间会串行，且不会与其它 collection 并发（同一 collection 内不并行）。
/// </para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EnvVarCollection
{
    public const string Name = "env-var-mutating";
}
