using System.Text;

namespace WindowCast.Server.Discovery;

/// <summary>
/// The macOS API identifies apps by bundle ID, which is URL-safe. On Windows the natural identity is an
/// image path or an AppUserModelId, so we wrap those in URL-safe base64 and expose that as "bundleID".
/// </summary>
public static class AppKey
{
    public static string Encode(string value)
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        return b64.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static string? Decode(string key)
    {
        try
        {
            var s = key.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4)
            {
                case 2: s += "=="; break;
                case 3: s += "="; break;
            }
            return Encoding.UTF8.GetString(Convert.FromBase64String(s));
        }
        catch
        {
            return null;
        }
    }
}
