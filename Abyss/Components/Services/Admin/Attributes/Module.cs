using System.Reflection;
using Abyss.Components.Services.Admin.Interfaces;

namespace Abyss.Components.Services.Admin.Attributes;

[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public class Module(int head) : Attribute
{
    public int Head { get; } = head;

    public static Type[] Modules
    {
        get
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            Type attributeType = typeof(Module);
            const string targetNamespace = "Abyss.Components.Services.Admin.Modules";
            
            var moduleTypes = assembly.GetTypes()
                .Where(t => t is { IsClass: true, IsAbstract: false, IsInterface: false })
                .Where(t => t.Namespace == targetNamespace)
                .Where(t => typeof(IModule).IsAssignableFrom(t))
                .Where(t => t.IsDefined(attributeType, inherit: false))
                .ToArray();

            return moduleTypes;
        }
    }
}