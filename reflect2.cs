using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

var paths = new[]
{
    @"C:\Users\david\.nuget\packages\agui.abstractions\0.0.5\lib\net8.0\AGUI.Abstractions.dll",
};
var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
var resolver = new PathAssemblyResolver(
    Directory.EnumerateFiles(runtimeDir, "*.dll")
        .Concat(paths)
        .Concat(new[]
        {
            @"C:\Users\david\.nuget\packages\microsoft.extensions.ai.abstractions\10.6.0\lib\net8.0\Microsoft.Extensions.AI.Abstractions.dll",
        })
        .Where(File.Exists));
using var mlc = new MetadataLoadContext(resolver);
var asm = mlc.LoadFromAssemblyPath(paths[0]);

foreach (var t in asm.GetTypes().Where(t => t.Name.Contains("Resume") || t.Name.Contains("Interrupt") || t.Name == "RunAgentInput").OrderBy(t => t.Name))
{
    Console.WriteLine("=== " + t.FullName + " ===");
    foreach (var p in t.GetProperties())
        Console.WriteLine($"  {p.PropertyType.Name} {p.Name}");
    foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
        Console.WriteLine($"  field {f.FieldType.Name} {f.Name}");
}
