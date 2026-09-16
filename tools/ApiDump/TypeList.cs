using System.Reflection;
static class TypeList
{
    public static void Run(string asmName, string[] filters)
    {
        var asm = Assembly.Load(asmName);
        foreach (var t in asm.GetExportedTypes().OrderBy(t => t.FullName))
            if (filters.Any(f => t.Name.Contains(f, StringComparison.OrdinalIgnoreCase)))
                Console.WriteLine(t.FullName + (t.IsEnum ? " [enum: " + string.Join(",", Enum.GetNames(t)) + "]" : t.IsInterface ? " [interface]" : ""));
    }
}
