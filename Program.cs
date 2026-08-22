using System;
using System.Linq;
using System.Reflection;
var asm = typeof(AGUI.Abstractions.RunAgentInput).Assembly;
foreach (var name in new[] { "RunAgentInput", "AGUIContext", "AGUIPayload", "AGUITextContent", "AGUIContent" })
{
    var t = asm.GetTypes().FirstOrDefault(x => x.Name == name);
    if (t is null) { Console.WriteLine("MISSING " + name); continue; }
    Console.WriteLine("TYPE: " + t.FullName + (t.IsEnum ? " enum" : ""));
    if (t.IsEnum) { Console.WriteLine("   values: " + string.Join(", ", Enum.GetNames(t))); continue; }
    foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        Console.WriteLine("   " + p.PropertyType.Name + " " + p.Name);
    foreach (var c in t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        Console.WriteLine("   ctor(" + string.Join(", ", c.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")");
}
