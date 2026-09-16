using System.Reflection;
if (args.Length > 1 && args[0] == "members") { Members.Run(args[1..]); return; }
if (args.Length > 1) { TypeList.Run(args[0], args[1..]); return; }

// Dumps the public members of the Vortice types we depend on, so the capture/encoder code uses exact names.
var wanted = new Dictionary<string, string[]>
{
    ["Vortice.Direct3D11"] = new[] { "Vortice.Direct3D11.D3D11", "Vortice.Direct3D11.ID3D11DeviceContext", "Vortice.Direct3D11.ID3D11Device", "Vortice.Direct3D11.Texture2DDescription", "Vortice.Direct3D11.MappedSubresource", "Vortice.Direct3D11.ID3D11Multithread", "Vortice.Direct3D11.Box" },
    ["Vortice.MediaFoundation"] = new[] { "Vortice.MediaFoundation.MediaFactory", "Vortice.MediaFoundation.IMFTransform", "Vortice.MediaFoundation.IMFMediaEventGenerator", "Vortice.MediaFoundation.IMFMediaEvent", "Vortice.MediaFoundation.IMFSample", "Vortice.MediaFoundation.IMFMediaBuffer", "Vortice.MediaFoundation.IMFMediaType", "Vortice.MediaFoundation.IMFAttributes", "Vortice.MediaFoundation.IMFActivate", "Vortice.MediaFoundation.MediaTypeAttributeKeys", "Vortice.MediaFoundation.VideoFormatGuids", "Vortice.MediaFoundation.MediaTypeGuids", "Vortice.MediaFoundation.TransformAttributeKeys", "Vortice.MediaFoundation.TransformCategoryGuids", "Vortice.MediaFoundation.TransformEnumFlag", "Vortice.MediaFoundation.MediaEventType", "Vortice.MediaFoundation.TransformMessageType", "Vortice.MediaFoundation.TransformOutputDataBufferFlags", "Vortice.MediaFoundation.TransformOutputDataBuffer", "Vortice.MediaFoundation.OutputStreamInfo", "Vortice.MediaFoundation.ICodecAPI", "Vortice.MediaFoundation.CodecApiPropertyKeys", "Vortice.MediaFoundation.SampleAttributeKeys", "Vortice.MediaFoundation.RegisterTypeInformation", "Vortice.MediaFoundation.MediaAttributeKey" },
};

var flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
foreach (var (asmName, types) in wanted)
{
    var asm = Assembly.Load(asmName);
    foreach (var tn in types)
    {
        var t = asm.GetType(tn);
        if (t is null)
        {
            Console.WriteLine($"### {tn}: NOT FOUND. Similar: " + string.Join(", ", asm.GetTypes().Where(x => x.Name.Contains(tn.Split('.').Last().TrimStart('I'), StringComparison.OrdinalIgnoreCase)).Select(x => x.FullName).Take(15)));
            continue;
        }
        Console.WriteLine($"### {t.FullName} : {t.BaseType?.Name}");
        if (t.IsEnum) { Console.WriteLine("   " + string.Join(", ", Enum.GetNames(t))); continue; }
        foreach (var m in t.GetMembers(flags).OrderBy(m => m.Name))
        {
            switch (m)
            {
                case MethodInfo mi when !mi.IsSpecialName:
                    Console.WriteLine($"   {mi.ReturnType.Name} {mi.Name}({string.Join(", ", mi.GetParameters().Select(p => (p.IsOut ? "out " : p.ParameterType.IsByRef ? "ref " : "") + p.ParameterType.Name.TrimEnd('&') + " " + p.Name))})");
                    break;
                case PropertyInfo pi:
                    Console.WriteLine($"   {pi.PropertyType.Name} {pi.Name} {{{(pi.CanRead ? "get;" : "")}{(pi.CanWrite ? "set;" : "")}}}");
                    break;
                case FieldInfo fi:
                    Console.WriteLine($"   field {fi.FieldType.Name} {fi.Name}");
                    break;
            }
        }
    }
}
