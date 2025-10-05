using System.Reflection;
using abyssctl.App.Interfaces;
using CommandLine;

namespace abyssctl.App.Attributes;

[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public class ModuleAttribute(int head) : Attribute
{
    public int Head { get; } = head;
    
    public static Type[] Modules
    {
        get
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            const string targetNamespace = "abyssctl.App.Modules";
            
            return assembly.GetTypes()
                .Where(t => t is { IsClass: true, IsAbstract: false, IsInterface: false })
                .Where(t => t.Namespace == targetNamespace)
                .Where(t => typeof(IOptions).IsAssignableFrom(t))
                .Where(t => t.IsDefined(typeof(VerbAttribute), inherit: true))
                .Where(t => t.IsDefined(typeof(ModuleAttribute), inherit: false))
                .ToArray();
        }
    }
}