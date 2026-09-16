using System.Reflection;
static class Members
{
    public static void Run(string[] fullNames)
    {
        var flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (var fn in fullNames)
        {
            var asmName = fn.StartsWith("Vortice.MediaFoundation") ? "Vortice.MediaFoundation" : fn.StartsWith("Vortice.Direct3D11") ? "Vortice.Direct3D11" : fn.StartsWith("Vortice.DXGI") ? "Vortice.DXGI" : "Vortice.Win32";
            var t = Assembly.Load(asmName).GetType(fn);
            Console.WriteLine($"### {fn} => {(t is null ? "NOT FOUND" : t.BaseType?.Name)}");
            if (t is null) continue;
            if (t.IsEnum) { Console.WriteLine("   " + string.Join(", ", Enum.GetNames(t))); continue; }
            foreach (var m in t.GetMembers(flags).OrderBy(m => m.Name))
                Console.WriteLine(m switch
                {
                    MethodInfo mi when !mi.IsSpecialName => $"   {mi.ReturnType.Name} {mi.Name}({string.Join(", ", mi.GetParameters().Select(p => (p.IsOut ? "out " : p.ParameterType.IsByRef ? "ref " : "") + p.ParameterType.Name.TrimEnd('&') + " " + p.Name))})",
                    PropertyInfo pi => $"   {pi.PropertyType.Name} {pi.Name} {{{(pi.CanRead ? "get;" : "")}{(pi.CanWrite ? "set;" : "")}}}",
                    FieldInfo fi => $"   field {fi.FieldType.Name} {fi.Name}",
                    ConstructorInfo ci => $"   ctor({string.Join(", ", ci.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})",
                    _ => "",
                });
        }
    }
}
