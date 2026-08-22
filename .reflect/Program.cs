using System;
using System.Linq;
using System.Reflection;
var asm = Assembly.LoadFrom(@"C:/Users/david/.nuget/packages/agui.server/0.0.3/lib/net10.0/AGUI.Server.dll");
foreach (var tn in new[] { "AGUI.Server.RunAgentInputExtensions", "AGUI.Server.RunFinishedEventExtensions", "AGUI.Server.ChatResponseUpdateAGUIExtensions" })
{
    var t = asm.GetType(tn);
    if (t is null) continue;
    Console.WriteLine($"=== {tn} ===");
    foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        Console.WriteLine($"  {m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(pi => pi.ParameterType.Name + " " + pi.Name))})");
}
