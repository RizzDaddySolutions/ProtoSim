using System;
using System.Linq;
using System.Reflection;
using AvalonDock.Serialization;

var assembly = typeof(LayoutDtoMapper).Assembly;
Console.WriteLine(assembly.FullName);
foreach (var t in assembly.GetTypes().Where(t => t.FullName!.Contains("Serialization") || t.Name.Contains("Serializer") || t.Name.Contains("Dto")))
{
    Console.WriteLine("TYPE " + t.FullName);
    foreach (var c in t.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        Console.WriteLine("  C " + c);
    foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        Console.WriteLine("  M " + m);
    foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        Console.WriteLine("  P " + p.PropertyType.Name + " " + p.Name);
}
