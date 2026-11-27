using System;
using System.Linq;
using System.Reflection;

internal static class Dumper
{
    private static void Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("uso: dumper <ensamblado.dll> [filtro]");
            return;
        }

        Assembly assembly = Assembly.LoadFrom(args[0]);
        string filter = args.Length > 1 ? args[1] : null;

        foreach (Type type in assembly.GetExportedTypes().OrderBy(t => t.FullName))
        {
            if (filter != null && type.FullName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            Console.WriteLine("TYPE " + type.FullName);

            foreach (ConstructorInfo ctor in type.GetConstructors())
                Console.WriteLine("  ctor " + string.Join(", ", ctor.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)));

            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
                Console.WriteLine("  prop " + property.PropertyType.Name + " " + property.Name);

            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static).Where(m => !m.IsSpecialName))
                Console.WriteLine("  meth " + method.ReturnType.Name + " " + method.Name + "(" + string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")");

            Console.WriteLine();
        }
    }
}
