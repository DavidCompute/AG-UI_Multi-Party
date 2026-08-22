using System;
using System.Linq;
using System.Reflection;
var asm = Assembly.LoadFrom(@"C:/Users/david/.nuget/packages/agui.abstractions/0.0.5/lib/net10.0/AGUI.Abstractions.dll");
foreach (var tn in new[] { "AGUI.Abstractions.RunAgentInput", "AGUI.Abstractions.AGUIResume", "AGUI.Abstractions.AGUIToolApprovalResumePayload", "AGUI.Abstractions.AGUIToolApprovalPayload", "AGUI.Abstractions.AGUIMessage", "AGUI.Abstractions.AGUIContext" })
{
    var t = asm.GetType(tn);
    if (t is null) { Console.WriteLine($"!! 类型不存在: {tn}"); continue; }
    Console.WriteLine($"=== {tn} ===");
    foreach (var p in t.GetProperties())
        Console.WriteLine($"  {p.PropertyType.Name} {p.Name}");
    foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
        Console.WriteLine($"  field {f.FieldType.Name} {f.Name}");
}
