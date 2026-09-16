using DailyMusings.Domain.Common;
using DailyMusings.Domain.Reflections;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Domain.Tests.Reflections;

/// <summary>
/// docs/开发指导.md §6.4 and §9.3: the draft page lets the user switch between the three version positions.
/// The invariant that matters is that the first version ever produced is never lost, because that is the only
/// record of what the model originally wrote.
/// </summary>
[TestClass]
public class WorkingVersionSwitchTests
{
    [TestMethod]
    public void Switching_to_the_previous_version_swaps_the_two_slots()
    {
        var (reflection, first, second, third) = BuildThreeVersions();

        reflection.SwitchWorkingVersion(second, TestFactory.Noon);

        Assert.AreEqual(second, reflection.WorkingVersionId);
        Assert.AreEqual(third, reflection.PreviousVersionId);
        Assert.AreEqual(first, reflection.InitialVersionId);
    }

    [TestMethod]
    public void Switching_is_reversible()
    {
        var (reflection, _, second, third) = BuildThreeVersions();

        reflection.SwitchWorkingVersion(second, TestFactory.Noon);
        reflection.SwitchWorkingVersion(third, TestFactory.Noon);

        Assert.AreEqual(third, reflection.WorkingVersionId);
        Assert.AreEqual(second, reflection.PreviousVersionId);
    }

    [TestMethod]
    public void The_initial_version_stays_pinned_when_it_becomes_the_working_version_again()
    {
        var (reflection, first, _, third) = BuildThreeVersions();

        reflection.SwitchWorkingVersion(first, TestFactory.Noon);

        Assert.AreEqual(first, reflection.WorkingVersionId);

        // The first version is a permanent record, so the pointer to it never moves even though the working
        // slot now points at it too.
        Assert.AreEqual(first, reflection.InitialVersionId);
        Assert.AreEqual(third, reflection.PreviousVersionId);
    }

    [TestMethod]
    public void A_version_pushed_out_of_the_three_positions_can_no_longer_be_selected()
    {
        var (reflection, first, second, third) = BuildThreeVersions();

        reflection.SwitchWorkingVersion(second, TestFactory.Noon); // previous = third
        reflection.SwitchWorkingVersion(first, TestFactory.Noon); // working = first, previous = second

        // §6.4 keeps exactly three positions, so the third version has just been displaced out of them.
        TestFactory.ThrowsDomain(
            "reflection.version.not_in_slots",
            () => reflection.SwitchWorkingVersion(third, TestFactory.Noon));
    }

    [TestMethod]
    public void Switching_to_the_version_already_in_the_working_slot_changes_nothing()
    {
        var (reflection, first, _, third) = BuildThreeVersions();

        reflection.SwitchWorkingVersion(third, TestFactory.Noon);

        Assert.AreEqual(third, reflection.WorkingVersionId);
        Assert.AreEqual(first, reflection.InitialVersionId);
    }

    [TestMethod]
    public void A_version_cannot_be_switched_while_a_generation_is_running()
    {
        var (reflection, _, second, _) = BuildThreeVersions();
        reflection.BeginGeneration(GenerationReason.Manual, TestFactory.Noon);

        TestFactory.ThrowsDomain(
            "reflection.version.switch_while_generating",
            () => reflection.SwitchWorkingVersion(second, TestFactory.Noon));
    }

    [TestMethod]
    public void A_day_without_a_version_cannot_switch()
    {
        var reflection = TestFactory.NewReflection();

        TestFactory.ThrowsDomain(
            "reflection.version.none",
            () => reflection.SwitchWorkingVersion(ReflectionVersionId.New(), TestFactory.Noon));
    }

    [TestMethod]
    public void An_empty_version_id_is_refused()
    {
        var (reflection, _, _, _) = BuildThreeVersions();

        TestFactory.ThrowsDomain(
            "reflection.version.empty_id",
            () => reflection.SwitchWorkingVersion(default, TestFactory.Noon));
    }

    /// <summary>Produces a draft that has been generated three times, so all three slots are distinct.</summary>
    private static (Reflection Reflection, ReflectionVersionId First, ReflectionVersionId Second, ReflectionVersionId Third)
        BuildThreeVersions()
    {
        var reflection = TestFactory.NewReflection();
        var first = TestFactory.NewVersion(reflection).Id;
        var second = TestFactory.NewVersion(reflection).Id;
        var third = TestFactory.NewVersion(reflection).Id;

        reflection.MarkReady(TestFactory.Noon);
        reflection.BeginGeneration(GenerationReason.Scheduled, TestFactory.Noon);
        reflection.ApplyGeneratedVersion(first, false, false, TestFactory.Noon);

        reflection.BeginGeneration(GenerationReason.Manual, TestFactory.Noon);
        reflection.ApplyGeneratedVersion(second, false, false, TestFactory.Noon);

        reflection.BeginGeneration(GenerationReason.Manual, TestFactory.Noon);
        reflection.ApplyGeneratedVersion(third, false, false, TestFactory.Noon);

        return (reflection, first, second, third);
    }
}
