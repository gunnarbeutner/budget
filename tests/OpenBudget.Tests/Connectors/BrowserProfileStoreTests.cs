using Microsoft.Extensions.Logging.Abstractions;
using OpenBudget.Browser;
using OpenBudget.Connectors;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests.Connectors;

/// <summary>
/// Long-lived browser profiles (BrowserProfileStore): stored encrypted without caches, restored for the same key only,
/// ignored when unreadable, and deleted with their bank login.
/// </summary>
public sealed class BrowserProfileStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ob-profile-tests-" + Guid.NewGuid().ToString("N"));
    private readonly SecretsProtector _secrets = new(SecretsProtector.GenerateKey());

    private string Dir(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private BrowserProfileStore Store(SecretsProtector? secrets = null) =>
        new(Path.Combine(_root, "store"), secrets ?? _secrets, NullLogger<BrowserProfileStore>.Instance);

    private static void Write(string dir, string relative, string content)
    {
        var path = Path.Combine(dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>A user-data directory with site data, caches and the running profile's guard files.</summary>
    private string Profile()
    {
        var dir = Dir("source");
        Write(dir, "Local State", "{\"state\":1}");
        Write(dir, "Default/Local Storage/leveldb/000003.log", "site-data-marker");
        Write(dir, "Default/IndexedDB/https_bank.example_0.indexeddb.leveldb/000001.log", "indexed");
        Write(dir, "Default/Cache/Cache_Data/f_000001", "cached");
        Write(dir, "Default/Code Cache/js/index", "code");
        Write(dir, "Default/Service Worker/CacheStorage/abc/1", "worker cache");
        Write(dir, "Default/Service Worker/Database/000001.log", "worker");
        Write(dir, "GrShaderCache/data_0", "shader");
        Write(dir, "SingletonLock", "host-1");
        Write(dir, "Default/Local Storage/leveldb/LOCK", "");
        return dir;
    }

    [Fact]
    public void A_profile_comes_back_with_its_site_data_but_without_caches()
    {
        var store = Store();
        var key = BrowserProfileStore.ForLogin(7);
        Assert.True(store.Save(key, Profile()));
        Assert.True(store.Exists(key));

        var target = Dir("target");
        Assert.True(store.Restore(key, target));
        Assert.Equal("site-data-marker", File.ReadAllText(Path.Combine(target, "Default/Local Storage/leveldb/000003.log")));
        Assert.True(File.Exists(Path.Combine(target, "Local State")));
        Assert.True(File.Exists(Path.Combine(target, "Default/IndexedDB/https_bank.example_0.indexeddb.leveldb/000001.log")));
        Assert.True(File.Exists(Path.Combine(target, "Default/Service Worker/Database/000001.log")));
        Assert.False(Directory.Exists(Path.Combine(target, "Default/Cache")));
        Assert.False(Directory.Exists(Path.Combine(target, "Default/Code Cache")));
        Assert.False(Directory.Exists(Path.Combine(target, "Default/Service Worker/CacheStorage")));
        Assert.False(Directory.Exists(Path.Combine(target, "GrShaderCache")));
        Assert.False(File.Exists(Path.Combine(target, "SingletonLock")));
        Assert.False(File.Exists(Path.Combine(target, "Default/Local Storage/leveldb/LOCK")));
    }

    [Fact]
    public void A_profile_travels_as_a_zip_to_a_browser_elsewhere_and_back_without_caches()
    {
        var store = Store();
        var key = BrowserProfileStore.ForLogin(8);
        Assert.Null(store.ReadArchive(key));
        Assert.True(store.Save(key, Profile()));

        var zip = store.ReadArchive(key)!;
        using (var archive = new System.IO.Compression.ZipArchive(new MemoryStream(zip)))
            Assert.Contains(archive.Entries, e => e.FullName == "Default/Local Storage/leveldb/000003.log");

        // the browser elsewhere hands back its whole directory, caches and guards included; only site data is kept
        var returned = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(returned, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(archive, "Default/Local Storage/leveldb/000003.log", "changed-marker");
            Add(archive, "Default/Cache/Cache_Data/f_000002", "cached");
            Add(archive, "SingletonLock", "host-2");
        }
        Assert.True(store.SaveArchive(key, returned.ToArray()));

        var target = Dir("target");
        Assert.True(store.Restore(key, target));
        Assert.Equal("changed-marker", File.ReadAllText(Path.Combine(target, "Default/Local Storage/leveldb/000003.log")));
        Assert.False(Directory.Exists(Path.Combine(target, "Default/Cache")));
        Assert.False(File.Exists(Path.Combine(target, "SingletonLock")));
    }

    [Fact]
    public void A_returned_archive_with_an_entry_outside_its_directory_is_not_stored()
    {
        var store = Store();
        var key = BrowserProfileStore.ForLogin(9);
        var returned = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(returned, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            Add(archive, "../escaped", "x");
        Assert.False(store.SaveArchive(key, returned.ToArray()));
        Assert.False(store.Exists(key));
        Assert.False(store.SaveArchive(key, [1, 2, 3]));
    }

    private static void Add(System.IO.Compression.ZipArchive archive, string name, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open());
        writer.Write(content);
    }

    [Fact]
    public void The_stored_file_is_encrypted()
    {
        var store = Store();
        var key = BrowserProfileStore.ForLogin(8);
        Assert.True(store.Save(key, Profile()));
        var file = Directory.GetFiles(Path.Combine(_root, "store")).Single();
        var bytes = File.ReadAllBytes(file);
        // neither the zip header nor its file names are readable
        Assert.False(bytes.AsSpan().StartsWith("PK"u8));
        Assert.Equal(-1, bytes.AsSpan().IndexOf("Local State"u8));
    }

    [Fact]
    public void A_profile_is_read_only_for_its_own_key_and_with_the_same_secrets_key()
    {
        var store = Store();
        var key = BrowserProfileStore.ForLogin(9);
        Assert.True(store.Save(key, Profile()));
        var other = BrowserProfileStore.ForLogin(10);
        var storeDir = Path.Combine(_root, "store");
        File.Copy(Path.Combine(storeDir, key + ".profile"), Path.Combine(storeDir, other + ".profile"));

        var target = Dir("other-key");
        Assert.False(store.Restore(other, target));
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));

        var rotated = Dir("rotated");
        Assert.False(Store(new SecretsProtector(SecretsProtector.GenerateKey())).Restore(key, rotated));
        Assert.Empty(Directory.EnumerateFileSystemEntries(rotated));
    }

    [Fact]
    public void A_damaged_profile_is_ignored_and_the_browser_starts_fresh()
    {
        var store = Store();
        var key = BrowserProfileStore.ForLogin(11);
        Assert.True(store.Save(key, Profile()));
        var file = Path.Combine(_root, "store", key + ".profile");
        var bytes = File.ReadAllBytes(file);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(file, bytes);

        var target = Dir("damaged");
        Assert.False(store.Restore(key, target));
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));
        Assert.False(store.Restore(BrowserProfileStore.ForLogin(12), Dir("missing")));
    }

    [Fact]
    public void Without_a_secrets_key_nothing_is_stored()
    {
        var store = Store(new SecretsProtector((string?)null));
        Assert.False(store.IsEnabled);
        Assert.False(store.Save(BrowserProfileStore.ForLogin(13), Profile()));
        Assert.False(Directory.Exists(Path.Combine(_root, "store")) && Directory.EnumerateFiles(Path.Combine(_root, "store")).Any());
    }

    [Fact]
    public async Task Deleting_a_login_deletes_its_browser_profile_and_the_owner_can_reset_it()
    {
        await using var w = await BankWorld.CreateAsync();
        var first = await w.AddLoginAsync(BankWorld.Alice);
        var second = await w.AddLoginAsync(BankWorld.Alice);
        var source = Profile();
        Assert.True(w.BrowserProfiles.Save(BrowserProfileStore.ForLogin(first), source));
        Assert.True(w.BrowserProfiles.Save(BrowserProfileStore.ForLogin(second), source));
        var alice = w.For(BankWorld.Alice);
        Assert.True(alice.HasBrowserProfile(first));

        // only the owner may reset it
        await Assert.ThrowsAsync<AccessDeniedException>(() => w.For(BankWorld.Bob).ResetBrowserProfileAsync(first));
        Assert.True(alice.HasBrowserProfile(first));
        await alice.ResetBrowserProfileAsync(first);
        Assert.False(alice.HasBrowserProfile(first));

        await alice.DeleteLoginAsync(second);
        Assert.False(w.BrowserProfiles.Exists(BrowserProfileStore.ForLogin(second)));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }
}
