using System.Text;
using DailyMusings.Domain.Common;
using DailyMusings.Domain.Time;
using DailyMusings.Infrastructure.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Infrastructure.Tests;

/// <summary>
/// The audio blob store (docs/开发指导.md §8.2, §15.1). These are the properties the capture loop depends on:
/// a blob is durable before it is referenced, its name is derived from the content type rather than from anything
/// a client sent, and deleting it actually removes it.
/// </summary>
[TestClass]
public class FileAudioStoreTests
{
    private static async Task<(InstancePaths Paths, FileAudioStore Store, string Root)> ArrangeAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "dailymusings-audio", Guid.CreateVersion7().ToString("N"));
        var paths = new InstancePaths(new StorageOptions { StatePath = root, MarkdownRootPath = Path.Combine(root, "content") });
        paths.EnsureCreated();

        await Task.CompletedTask;
        return (paths, new FileAudioStore(paths), root);
    }

    private static Stream Bytes(string content) => new MemoryStream(Encoding.UTF8.GetBytes(content));

    [TestMethod]
    public async Task A_saved_blob_is_on_disk_and_can_be_deleted()
    {
        var (paths, store, root) = await ArrangeAsync();

        try
        {
            var id = InputEntryId.New();
            var day = ContentDate.From(new DateOnly(2026, 3, 7));

            var stored = await store.SaveAsync(id, day, Bytes("audio-bytes"), "audio/mp4", CancellationToken.None);

            Assert.AreEqual($"2026/03/{id}.m4a", stored.RelativePath);
            Assert.AreEqual(11, stored.ByteCount);
            Assert.AreEqual("audio/mp4", stored.ContentType);

            var absolute = paths.ResolveMediaFile(stored.RelativePath);
            Assert.IsTrue(File.Exists(absolute), $"Expected the blob at {absolute}");
            Assert.IsTrue(await store.ExistsAsync(stored.RelativePath, CancellationToken.None));
            Assert.AreEqual(11L, await store.GetSizeAsync(stored.RelativePath, CancellationToken.None));

            await store.DeleteAsync(stored.RelativePath, CancellationToken.None);

            Assert.IsFalse(File.Exists(absolute), "Deleting must actually remove the blob from the volume.");
            Assert.IsFalse(await store.ExistsAsync(stored.RelativePath, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task A_blob_can_be_read_back_byte_for_byte()
    {
        var (_, store, root) = await ArrangeAsync();

        try
        {
            var stored = await store.SaveAsync(
                InputEntryId.New(),
                ContentDate.From(new DateOnly(2026, 3, 7)),
                Bytes("round-trip"),
                "audio/wav",
                CancellationToken.None);

            await using var read = await store.OpenReadAsync(stored.RelativePath, CancellationToken.None);
            using var buffer = new MemoryStream();
            await read.CopyToAsync(buffer);

            Assert.AreEqual("round-trip", Encoding.UTF8.GetString(buffer.ToArray()));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task The_stored_extension_comes_from_the_content_type_never_from_the_client()
    {
        var (_, store, root) = await ArrangeAsync();

        try
        {
            var day = ContentDate.From(new DateOnly(2026, 3, 7));

            // A hostile or simply odd content type lands in the safe default bucket rather than becoming a path.
            foreach (var (contentType, expected) in new[]
            {
                ("audio/mp4", ".m4a"),
                ("audio/aac", ".aac"),
                ("audio/webm; codecs=opus", ".webm"),
                ("audio/ogg", ".ogg"),
                ("../../etc/passwd", ".bin"),
                (null, ".bin"),
            })
            {
                var stored = await store.SaveAsync(InputEntryId.New(), day, Bytes("x"), contentType, CancellationToken.None);
                StringAssert.EndsWith(stored.RelativePath, expected, $"content type '{contentType}'");
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task An_empty_upload_is_refused_and_leaves_nothing_behind()
    {
        var (paths, store, root) = await ArrangeAsync();

        try
        {
            var id = InputEntryId.New();
            var day = ContentDate.From(new DateOnly(2026, 3, 7));

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                store.SaveAsync(id, day, Bytes(string.Empty), "audio/mp4", CancellationToken.None));

            // Neither the blob nor the partial file may survive a refused upload.
            Assert.AreEqual(
                0,
                Directory.EnumerateFiles(paths.MediaPath, "*", SearchOption.AllDirectories).Count());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Deleting_a_blob_that_is_already_gone_is_not_an_error()
    {
        var (_, store, root) = await ArrangeAsync();

        try
        {
            // §9.2 retries mean a delete can legitimately run twice.
            await store.DeleteAsync("2026/03/does-not-exist.m4a", CancellationToken.None);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
