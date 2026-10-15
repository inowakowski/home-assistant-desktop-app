using HADA.Service.Settings;

namespace HADA.Tests.Service;

/// <summary>How the service keeps passwords and tokens where the system has no protection of its own for it.</summary>
public sealed class KeyFileSecretProtectorTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "hada-keys-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void A_secret_comes_back_as_it_went_in_and_reads_as_nothing_like_it()
    {
        var protector = new KeyFileSecretProtector(KeyFile("a"));

        var sealedSecret = protector.Protect("zażółć gęślą jaźń");

        Assert.DoesNotContain("gęślą", sealedSecret, StringComparison.Ordinal);
        Assert.NotEqual(sealedSecret, protector.Protect("zażółć gęślą jaźń"));
        Assert.Equal("zażółć gęślą jaźń", protector.TryUnprotect(sealedSecret));
        Assert.Equal("zażółć gęślą jaźń", new KeyFileSecretProtector(KeyFile("a")).TryUnprotect(sealedSecret));
    }

    [Fact]
    public void Without_its_key_a_secret_is_nothing()
    {
        var sealedSecret = new KeyFileSecretProtector(KeyFile("a")).Protect("token");

        Assert.Null(new KeyFileSecretProtector(KeyFile("b")).TryUnprotect(sealedSecret));
        Assert.False(File.Exists(KeyFile("b")));

        new KeyFileSecretProtector(KeyFile("b")).Protect("other");
        Assert.Null(new KeyFileSecretProtector(KeyFile("b")).TryUnprotect(sealedSecret));
    }

    [Fact]
    public void Text_that_was_not_sealed_here_is_refused()
    {
        var protector = new KeyFileSecretProtector(KeyFile("a"));
        var sealedSecret = protector.Protect("token");

        Assert.Null(protector.TryUnprotect(null));
        Assert.Null(protector.TryUnprotect(string.Empty));
        Assert.Null(protector.TryUnprotect("token"));
        Assert.Null(protector.TryUnprotect("k1:not-base64!"));
        Assert.Null(protector.TryUnprotect(sealedSecret[..^4] + "AAAA"));
    }

    [Fact]
    public void Only_its_user_may_read_the_key()
    {
        new KeyFileSecretProtector(KeyFile("a")).Protect("token");

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(KeyFile("a")));
        }
    }

    private string KeyFile(string name) => Path.Combine(_folder, name, KeyFileSecretProtector.FileName);
}
