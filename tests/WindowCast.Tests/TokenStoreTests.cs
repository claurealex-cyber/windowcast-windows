using System.Text.RegularExpressions;
using WindowCast.Server.Auth;

namespace WindowCast.Tests;

public class TokenStoreTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), "windowcast-tests", Guid.NewGuid().ToString("n"), "token");

    [Fact]
    public void Generates_48_hex_chars_and_persists()
    {
        var path = TempPath();
        var store = new TokenStore(path);
        Assert.Matches(new Regex("^[0-9a-f]{48}$"), store.Current);
        Assert.Equal(store.Current, File.ReadAllText(path).Trim());

        var reloaded = new TokenStore(path);
        Assert.Equal(store.Current, reloaded.Current);
    }

    [Fact]
    public void Rotate_writes_a_new_token_and_raises_event()
    {
        var path = TempPath();
        var store = new TokenStore(path);
        var old = store.Current;
        var raised = 0;
        store.Rotated += () => raised++;

        var fresh = store.Rotate();

        Assert.NotEqual(old, fresh);
        Assert.Equal(1, raised);
        Assert.Equal(fresh, File.ReadAllText(path).Trim());
    }

    [Fact]
    public void Empty_file_is_replaced()
    {
        var path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "  \n");
        var store = new TokenStore(path);
        Assert.Equal(48, store.Current.Length);
    }
}
