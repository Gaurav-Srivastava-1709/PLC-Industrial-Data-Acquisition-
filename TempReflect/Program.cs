using System;
using System.Reflection;
using Wpf.Ui.Appearance;

class Program
{
    static void Main()
    {
        var type = typeof(ApplicationThemeManager);
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (method.Name == "Apply")
            {
                Console.Write("Apply(");
                var parameters = method.GetParameters();
                for (int i = 0; i < parameters.Length; i++)
                {
                    Console.Write($"{parameters[i].ParameterType.Name} {parameters[i].Name}");
                    if (i < parameters.Length - 1) Console.Write(", ");
                }
                Console.WriteLine(")");
            }
        }
    }
}
