
using System;
using System.Linq;
using System.Reflection;
var asm = Assembly.LoadFrom(@"C:/Users/david/.nuget/packages/pdfsharp/6.2.4/lib/net8.0/PDFsharp.dll");
var cr = asm.GetType("PdfSharp.Pdf.Content.ContentReader");
foreach (var m in cr.GetMethods().Where(m => m.Name == "ReadContent"))
    Console.WriteLine("ReadContent -> " + m.ReturnType);
var co = asm.GetType("PdfSharp.Pdf.Content.Objects.COperator");
foreach (var p in co.GetProperties()) Console.WriteLine("COperator." + p.Name + " : " + p.PropertyType);
var cs = asm.GetType("PdfSharp.Pdf.Content.Objects.CString");
foreach (var p in cs.GetProperties()) Console.WriteLine("CString." + p.Name + " : " + p.PropertyType);
var ca = asm.GetType("PdfSharp.Pdf.Content.Objects.CArray");
foreach (var p in ca.GetProperties()) Console.WriteLine("CArray." + p.Name + " : " + p.PropertyType);
