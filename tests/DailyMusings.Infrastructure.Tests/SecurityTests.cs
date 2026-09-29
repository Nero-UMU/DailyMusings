using DailyMusings.Application.Abstractions;
using DailyMusings.Infrastructure.Security;
using DailyMusings.Infrastructure.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Infrastructure.Tests;

/// <summary>
/// docs/开发指导.md §10.1, §10.2 and §10.4: strong password hashing, high-entropy tokens, and secrets that are
/// resolved by name and never logged.
/// </summary>
[TestClass]
public class SecurityTests
{
    /// <summary>A low iteration count keeps the suite fast; production uses the default.</summary>
    private static Pbkdf2PasswordHasher Hasher => new(iterations: 1_000);

    [TestMethod]
    public void A_password_verifies_against_its_hash_and_nothing_else()
    {
        var hash = Hasher.Hash("CorrectHorseBattery1");

        Assert.IsTrue(Hasher.Verify("CorrectHorseBattery1", hash));
        Assert.IsFalse(Hasher.Verify("correcthorsebattery1", hash));
        Assert.IsFalse(Hasher.Verify("", hash));
        Assert.IsFalse(Hasher.Verify("CorrectHorseBattery1 ", hash));
    }

    [TestMethod]
    public void The_same_password_hashes_differently_every_time()
    {
        var first = Hasher.Hash("CorrectHorseBattery1");
        var second = Hasher.Hash("CorrectHorseBattery1");

        Assert.AreNotEqual(first, second, "Each hash must carry its own salt.");
        Assert.IsTrue(Hasher.Verify("CorrectHorseBattery1", first));
        Assert.IsTrue(Hasher.Verify("CorrectHorseBattery1", second));
    }

    [TestMethod]
    public void The_stored_format_records_the_algorithm_and_cost()
    {
        var hash = Hasher.Hash("CorrectHorseBattery1");
        var parts = hash.Split('$');

        Assert.AreEqual(4, parts.Length);
        Assert.AreEqual("pbkdf2-sha256", parts[0]);
        Assert.AreEqual("1000", parts[1], "The iteration count must be recorded so it can be raised later.");
    }

    [TestMethod]
    public void A_malformed_stored_hash_fails_authentication_instead_of_throwing()
    {
        Assert.IsFalse(Hasher.Verify("x", string.Empty));
        Assert.IsFalse(Hasher.Verify("x", "not-a-hash"));
        Assert.IsFalse(Hasher.Verify("x", "pbkdf2-sha256$notanumber$c2FsdA==$aGFzaA=="));
        Assert.IsFalse(Hasher.Verify("x", "bcrypt$1000$c2FsdA==$aGFzaA=="));
        Assert.IsFalse(Hasher.Verify("x", "pbkdf2-sha256$1000$!!!not-base64!!!$aGFzaA=="));
    }

    [TestMethod]
    public void Device_tokens_are_long_random_and_case_sensitive()
    {
        var generator = new CryptoSecretGenerator();

        var first = generator.GenerateDeviceToken();
        var second = generator.GenerateDeviceToken();

        Assert.AreNotEqual(first.Plaintext, second.Plaintext);
        Assert.IsTrue(first.Plaintext.Length >= 43, "256 bits of base64url is at least 43 characters.");
        Assert.AreEqual(first.Hash, generator.HashToken(first.Plaintext));

        // A token is machine-generated and machine-sent, so its lookup hash must not normalize casing.
        Assert.AreNotEqual(generator.HashToken("AbC"), generator.HashToken("abc"));
    }

    [TestMethod]
    public void A_pairing_code_can_be_retyped_with_different_casing_or_without_the_separator()
    {
        var generator = new CryptoSecretGenerator();
        var code = generator.GeneratePairingCode();

        Assert.AreEqual(9, code.Plaintext.Length, "Eight characters plus the visual separator.");
        Assert.AreEqual('-', code.Plaintext[4]);

        var expected = code.Hash;
        Assert.AreEqual(expected, generator.HashPairingCode(code.Plaintext));
        Assert.AreEqual(expected, generator.HashPairingCode(code.Plaintext.ToLowerInvariant()));
        Assert.AreEqual(expected, generator.HashPairingCode(code.Plaintext.Replace("-", string.Empty)));
        Assert.AreEqual(expected, generator.HashPairingCode($"  {code.Plaintext}  "));
    }

    [TestMethod]
    public void Generated_secrets_avoid_characters_people_misread()
    {
        var generator = new CryptoSecretGenerator();

        for (var i = 0; i < 20; i++)
        {
            var password = generator.GenerateInitialPassword();
            var compact = CryptoSecretGenerator.NormalizePairingCode(generator.GeneratePairingCode().Plaintext);

            Assert.AreEqual(24, password.Length);
            Assert.IsTrue(CryptoSecretGenerator.HumanAlphabet.Contains(password[0], StringComparison.Ordinal));
            Assert.IsFalse(compact.Contains('I', StringComparison.Ordinal));
            Assert.IsFalse(compact.Contains('O', StringComparison.Ordinal));
            Assert.IsFalse(compact.Contains('0', StringComparison.Ordinal));
            Assert.IsFalse(compact.Contains('1', StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// Appendix A.27: the deployment cannot provide a credential any more. A file where the store used to look,
    /// and the environment variable it used to read, must both be invisible — otherwise "a key cannot be
    /// injected through compose" is a claim the code does not keep. The admin page's encrypted store is the one
    /// route, and it still answers.
    /// </summary>
    [TestMethod]
    public async Task A_secret_can_only_come_from_the_admin_pages_encrypted_store()
    {
        var root = Path.Combine(Path.GetTempPath(), "dailymusings-secrets", Guid.CreateVersion7().ToString("N"));
        var secretsDirectory = Path.Combine(root, "secrets");
        Directory.CreateDirectory(secretsDirectory);

        try
        {
            File.WriteAllText(Path.Combine(secretsDirectory, "smtp-password"), "s3cret-from-file\n");
            Environment.SetEnvironmentVariable("DAILYMUSINGS_SECRET_EMBEDDING_API_KEY", "from-env");

            // KeyRingPath is set explicitly: with the default ("keys") the encrypted store would write into this
            // test process's working directory, and the next run would read its own leftover value back.
            var store = new EncryptedUiSecretStore(new InstancePaths(new StorageOptions
            {
                RootPath = root,
                KeyRingPath = Path.Combine(root, "keys"),
            }));
            var secrets = (ISecretStore)store;

            Assert.IsNull(secrets.TryGet("smtp-password"), "A mounted file must not be read any more.");
            Assert.IsNull(secrets.TryGet("embedding-api-key"), "An environment variable must not be read any more.");
            Assert.AreEqual(SecretSource.None, secrets.ResolveSource("smtp-password"));
            Assert.IsFalse(secrets.Exists("smtp-password"));

            await ((IUiSecretStore)store).SetAsync("smtp-password", "typed", CancellationToken.None);

            Assert.AreEqual("typed", secrets.TryGet("smtp-password"));
            Assert.AreEqual(SecretSource.Ui, secrets.ResolveSource("smtp-password"));
            Assert.IsTrue(secrets.Exists("smtp-password"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DAILYMUSINGS_SECRET_EMBEDDING_API_KEY", null);
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void A_secret_name_cannot_escape_the_credential_directory()
    {
        // Nothing is written here, so a temp root and a temp key-ring path keep the store off this process's
        // working directory either way.
        var root = Path.Combine(Path.GetTempPath(), "dailymusings-secret-names", Guid.CreateVersion7().ToString("N"));

        try
        {
            var store = (ISecretStore)new EncryptedUiSecretStore(new InstancePaths(new StorageOptions
            {
                RootPath = root,
                KeyRingPath = Path.Combine(root, "keys"),
            }));

            Assert.ThrowsException<ArgumentException>(() => store.TryGet("../../etc/passwd"));
            Assert.ThrowsException<ArgumentException>(() => store.TryGet("nested/name"));
            Assert.ThrowsException<ArgumentException>(() => store.TryGet("   "));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public void The_instance_creates_its_directory_contract()
    {
        var root = Path.Combine(Path.GetTempPath(), "dailymusings-paths", Guid.CreateVersion7().ToString("N"));

        try
        {
            var paths = new InstancePaths(new StorageOptions { RootPath = root });
            paths.EnsureCreated();

            foreach (var directory in paths.ManagedDirectories)
            {
                Assert.IsTrue(Directory.Exists(directory), $"Missing directory: {directory}");
            }

            StringAssert.EndsWith(paths.DatabasePath, InstancePaths.DatabaseFileName);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
