using DailyMusings.Application.Abstractions;
using DailyMusings.Application.Inputs;
using DailyMusings.Domain.Common;
using DailyMusings.Domain.Reflections;
using DailyMusings.Domain.Time;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Infrastructure.Tests;

[TestClass]
public sealed class InputDeletionTests
{
    [TestMethod]
    public async Task Manual_delete_removes_the_entry_and_its_article_source_links()
    {
        await using var context = await ReflectionTestContext.CreateAsync();
        var entry = await context.CaptureTextAsync("准备删除的手写记录。");
        await context.SeedDraftAsync(context.Today, ReflectionStatus.Confirmed);

        Assert.AreEqual(1, await context.Database.CountAsync("source_reference"));

        var delete = new DeleteInputUseCase(context.Inputs, new NoAudioStore());
        await delete.ExecuteAsync(entry.Id, CancellationToken.None);

        Assert.IsNull(await context.Inputs.FindByIdAsync(entry.Id, CancellationToken.None));
        Assert.AreEqual(0, await context.Database.CountAsync("source_reference"));
        Assert.AreEqual(1, await context.Database.CountAsync("reflection"), "删除素材不能删除已生成的文章正文。");
    }

    private sealed class NoAudioStore : IAudioStore
    {
        public Task<StoredAudio> SaveAsync(
            InputEntryId inputId,
            ContentDate contentDate,
            Stream content,
            string? contentType,
            CancellationToken cancellationToken) => throw new AssertFailedException("No audio operation was expected.");

        public Task<Stream> OpenReadAsync(string storedPath, CancellationToken cancellationToken) =>
            throw new AssertFailedException("No audio operation was expected.");

        public Task<bool> ExistsAsync(string storedPath, CancellationToken cancellationToken) =>
            throw new AssertFailedException("No audio operation was expected.");

        public Task DeleteAsync(string storedPath, CancellationToken cancellationToken) =>
            throw new AssertFailedException("No audio operation was expected.");

        public Task<long?> GetSizeAsync(string storedPath, CancellationToken cancellationToken) =>
            throw new AssertFailedException("No audio operation was expected.");
    }
}
