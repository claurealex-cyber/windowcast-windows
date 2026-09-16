using System.Security.Cryptography;

namespace WindowCast.Server.Auth;

/// <summary>
/// Persists the single access token (48 lowercase hex chars) to disk, generating one on first run.
/// Mirrors ~/.windowcast-token on macOS; default location is %LOCALAPPDATA%\WindowCast\token.
/// </summary>
public sealed class TokenStore
{
    public const int TokenBytes = 24;

    public string FilePath { get; }
    public string Current { get; private set; }

    /// <summary>Raised after <see cref="Rotate"/> replaces the token.</summary>
    public event Action? Rotated;

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowCast", "token");

    public TokenStore(string? filePath = null)
    {
        FilePath = filePath ?? DefaultPath;
        Current = LoadOrGenerate();
    }

    public string Rotate()
    {
        if (File.Exists(FilePath)) File.Delete(FilePath);
        Current = LoadOrGenerate();
        Rotated?.Invoke();
        return Current;
    }

    private string LoadOrGenerate()
    {
        if (File.Exists(FilePath))
        {
            var existing = File.ReadAllText(FilePath).Trim();
            if (existing.Length > 0) return existing;
        }

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(TokenBytes)).ToLowerInvariant();
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, token);
        return token;
    }
}
