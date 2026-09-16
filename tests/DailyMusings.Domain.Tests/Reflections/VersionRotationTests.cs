using DailyMusings.Domain.Common;
using DailyMusings.Domain.Reflections;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Domain.Tests.Reflections;

/// <summary>
/// docs/开发指导.md §6.4 and §17.1: rotation maintains exactly "最初、上一、当前", the first version is
/// permanent, and a hand-edited working version is never displaced without the user saying so.
/// </summary>
[TestClass]
public class VersionRotationTests
{
    /// <summary>Drives one full generation cycle and returns the version that landed in the working slot.</summary>
    private static (Reflection Reflection, ReflectionVersion Version) Generate(Reflection reflection)
    {
        switch (reflection.Status)
        {
            case ReflectionStatus.PendingInputs:
                reflection.MarkReady(TestFactory.Noon);
                reflection.BeginGeneration(GenerationReason.Scheduled, TestFactory.Noon);
                break;

            case ReflectionStatus.Ready:
                reflection.BeginGeneration(GenerationReason.Scheduled, TestFactory.Noon);
                break;

            case ReflectionStatus.StaleByLateInput:
                reflection.BeginGeneration(GenerationReason.LateInputRegeneration, TestFactory.Noon);
                break;

            case ReflectionStatus.ReviewRequired:
                reflection.BeginGeneration(GenerationReason.Manual, TestFactory.Noon);
                break;

            default:
                throw new InvalidOperationException($"Unexpected status {reflection.Status} for a generation cycle.");
        }

        var version = TestFactory.NewVersion(reflection);
        reflection.ApplyGeneratedVersion(version.Id, workingVersionHasManualEdits: false, userConfirmedOverwrite: false, TestFactory.Noon);
        return (reflection, version);
    }

    [TestMethod]
    public void The_first_version_is_kept_forever_across_repeated_regenerations()
    {
        var reflection = TestFactory.NewReflection();
        var (_, first) = Generate(reflection);
        var (_, second) = Generate(reflection);
        var (_, third) = Generate(reflection);

        Assert.AreEqual(first.Id, reflection.InitialVersionId);
        Assert.AreEqual(second.Id, reflection.PreviousVersionId);
        Assert.AreEqual(third.Id, reflection.WorkingVersionId);
    }

    [TestMethod]
    public void Rotation_moves_the_working_slot_to_previous_each_time()
    {
        var reflection = TestFactory.NewReflection();
        var (_, first) = Generate(reflection);
        Assert.IsNull(reflection.PreviousVersionId);

        var (_, second) = Generate(reflection);

        Assert.AreEqual(first.Id, reflection.PreviousVersionId);
        Assert.AreEqual(second.Id, reflection.WorkingVersionId);
        Assert.IsTrue(reflection.References(first.Id), "the first version must stay reachable");
    }

    /// <summary>The rule the doc calls out by name: 不得静默覆盖用户手工编辑.</summary>
    [TestMethod]
    public void A_hand_edited_working_version_blocks_regeneration_until_confirmed()
    {
        var reflection = TestFactory.NewReflection();
        var (_, working) = Generate(reflection);

        working.Edit("我改过的标题", "我改过的摘要", "我改过的正文。", TestFactory.Noon);
        Assert.IsTrue(working.HasManualEdits);

        reflection.BeginGeneration(GenerationReason.Manual, TestFactory.Noon);
        var replacement = TestFactory.NewVersion(reflection);

        TestFactory.ThrowsDomain(
            "reflection.regeneration.overwrites_manual_edits",
            () => reflection.ApplyGeneratedVersion(replacement.Id, working.HasManualEdits, userConfirmedOverwrite: false, TestFactory.Noon));

        // The draft is left exactly as it was — no half-applied rotation.
        Assert.AreEqual(ReflectionStatus.Generating, reflection.Status);
        Assert.AreEqual(working.Id, reflection.WorkingVersionId);
        Assert.IsNull(reflection.PreviousVersionId);

        // With the user's explicit consent (the client must have warned them) rotation proceeds, and the
        // edited version stays reachable in the previous slot rather than being destroyed.
        reflection.ApplyGeneratedVersion(replacement.Id, working.HasManualEdits, userConfirmedOverwrite: true, TestFactory.Noon);

        Assert.AreEqual(ReflectionStatus.ReviewRequired, reflection.Status);
        Assert.AreEqual(replacement.Id, reflection.WorkingVersionId);
        Assert.AreEqual(working.Id, reflection.PreviousVersionId);
        Assert.AreEqual(working.Id, reflection.InitialVersionId);
    }

    [TestMethod]
    public void An_unedited_working_version_rotates_without_asking()
    {
        var reflection = TestFactory.NewReflection();
        var (_, first) = Generate(reflection);
        var (_, second) = Generate(reflection);

        Assert.AreEqual(first.Id, reflection.PreviousVersionId);
        Assert.AreEqual(second.Id, reflection.WorkingVersionId);
    }

    [TestMethod]
    public void A_confirmed_version_pointer_survives_later_regeneration()
    {
        var reflection = TestFactory.NewReflection();
        var (_, first) = Generate(reflection);
        reflection.Confirm(first.Id, TestFactory.Noon);

        reflection.MarkStaleByLateInput(TestFactory.Noon);
        Generate(reflection);

        // ConfirmedVersionId still resolves, which is exactly why version rows are never deleted.
        Assert.AreEqual(first.Id, reflection.ConfirmedVersionId);
        Assert.IsTrue(reflection.References(first.Id));
    }

    [TestMethod]
    public void Applying_a_generated_version_requires_the_generating_state()
    {
        var reflection = TestFactory.NewReflection();
        reflection.MarkReady(TestFactory.Noon);
        var version = TestFactory.NewVersion(reflection);

        TestFactory.ThrowsDomain(
            "reflection.status.unexpected",
            () => reflection.ApplyGeneratedVersion(version.Id, false, false, TestFactory.Noon));
    }

    [TestMethod]
    public void An_empty_version_id_is_refused()
    {
        var reflection = TestFactory.NewReflection();
        reflection.MarkReady(TestFactory.Noon);
        reflection.BeginGeneration(GenerationReason.Scheduled, TestFactory.Noon);

        TestFactory.ThrowsDomain(
            "reflection.version.empty_id",
            () => reflection.ApplyGeneratedVersion(default, false, false, TestFactory.Noon));
    }

    [TestMethod]
    public void References_covers_all_four_slots()
    {
        var reflection = TestFactory.NewReflection();
        var (_, first) = Generate(reflection);
        var (_, second) = Generate(reflection);
        reflection.Confirm(second.Id, TestFactory.Noon);

        Assert.IsTrue(reflection.References(first.Id));
        Assert.IsTrue(reflection.References(second.Id));
        Assert.IsFalse(reflection.References(ReflectionVersionId.New()));
    }
}
