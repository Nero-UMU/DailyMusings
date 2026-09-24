using System.Text;
using DailyMusings.Infrastructure.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Infrastructure.Tests;

/// <summary>
/// The encrypted credential store (docs/开发指导.md §10.4 as revised). Three properties matter, and all three are
/// about what a leaked copy of the instance would contain: the plaintext is not on disk, the store lives outside
/// the backup set, and a value written by one key ring cannot be read by another.
/// </summary>
[TestClass]
public class UiSecretStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "dailymusings-ui-secrets",
        Guid.CreateVersion7().ToString("N"));

    [TestMethod]
    public async Task A_stored_value_round_trips()
    {
        var (store, _) = TestUiSecretStoreFactory.CreateReal(_root);

        await store.SetAsync("smtp-password", "s3cret-value", CancellationToken.None);

        Assert.AreEqual("s3cret-value", await store.GetAsync("smtp-password", CancellationToken.None));
        Assert.IsTrue(await store.ExistsAsync("smtp-password", CancellationToken.None));
        Assert.IsNull(await store.GetAsync("never-set", CancellationToken.None));
    }

    /// <summary>
    /// The point of encrypting at all: a backup is a copy of files, and this file is not one of them — but even a
    /// stray copy must not read as the password.
    /// </summary>
    [TestMethod]
    public async Task The_file_on_disk_never_contains_the_value()
    {
        var (store, paths) = TestUiSecretStoreFactory.CreateReal(_root);

        await store.SetAsync("smtp-password", "s3cret-value", CancellationToken.None);

        var file = Path.Combine(paths.UiSecretsPath, "smtp-password");
        Assert.IsTrue(File.Exists(file));

        var bytes = File.ReadAllBytes(file);

        Assert.IsFalse(
            Encoding.UTF8.GetString(bytes).Contains("s3cret-value", StringComparison.Ordinal),
            "A credential a grep can find is a credential that leaked with the volume.");
    }

    /// <summary>
    /// The store sits under the key ring directory, which the backup pipeline excludes (decision A.14). Written
    /// as a test because "it happens to be outside the backup set" is exactly the kind of property a later
    /// refactor moves without noticing.
    /// </summary>
    [TestMethod]
    public void The_store_is_outside_the_directories_a_backup_copies()
    {
        var (_, paths) = TestUiSecretStoreFactory.CreateReal(_root);

        Assert.IsFalse(
            paths.BackedUpDirectories.Any(directory =>
                paths.UiSecretsPath.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal)),
            "No directory a backup copies may contain the credential store.");
    }

    /// <summary>
    /// A value encrypted by a key ring that is gone is unreadable, and saying "not provisioned" is the honest
    /// answer — the alternative is failing every notification with a cryptographic error nobody can act on.
    /// </summary>
    [TestMethod]
    public async Task A_value_whose_key_disappeared_reads_as_not_provisioned()
    {
        var (store, paths) = TestUiSecretStoreFactory.CreateReal(_root);

        await store.SetAsync("smtp-password", "s3cret-value", CancellationToken.None);

        // Losing the key ring is what a restore onto a new volume looks like (A.13/A.14).
        File.Delete(Path.Combine(paths.UiSecretsPath, "ui-secrets.key"));

        var reloaded = new EncryptedUiSecretStore(paths);

        Assert.IsNull(await reloaded.GetAsync("smtp-password", CancellationToken.None));
        Assert.IsFalse(await reloaded.ExistsAsync("smtp-password", CancellationToken.None));

        // And the store is usable again afterwards, with a fresh key.
        await reloaded.SetAsync("smtp-password", "another-value", CancellationToken.None);
        Assert.AreEqual("another-value", await reloaded.GetAsync("smtp-password", CancellationToken.None));
    }

    [TestMethod]
    public async Task A_credential_cannot_be_renamed_onto_another_name()
    {
        var (store, paths) = TestUiSecretStoreFactory.CreateReal(_root);

        await store.SetAsync("smtp-password", "s3cret-value", CancellationToken.None);

        // The name is authenticated data, so moving the file to another secret's name must not decrypt.
        File.Copy(
            Path.Combine(paths.UiSecretsPath, "smtp-password"),
            Path.Combine(paths.UiSecretsPath, "openai-api-key"));

        Assert.IsNull(await store.GetAsync("openai-api-key", CancellationToken.None));
    }

    [TestMethod]
    public async Task Deleting_is_idempotent_and_the_name_can_be_reused()
    {
        var (store, _) = TestUiSecretStoreFactory.CreateReal(_root);

        await store.SetAsync("smtp-password", "one", CancellationToken.None);
        await store.DeleteAsync("smtp-password", CancellationToken.None);
        await store.DeleteAsync("smtp-password", CancellationToken.None);

        Assert.IsNull(await store.GetAsync("smtp-password", CancellationToken.None));

        await store.SetAsync("smtp-password", "two", CancellationToken.None);
        Assert.AreEqual("two", await store.GetAsync("smtp-password", CancellationToken.None));
    }

    [TestMethod]
    public async Task A_name_with_a_path_component_is_refused()
    {
        var (store, _) = TestUiSecretStoreFactory.CreateReal(_root);

        await Assert.ThrowsExceptionAsync<ArgumentException>(() =>
            store.SetAsync("../escape", "value", CancellationToken.None));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }
}
