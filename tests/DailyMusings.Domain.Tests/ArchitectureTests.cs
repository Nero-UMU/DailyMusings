using DailyMusings.Domain.Common;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Domain.Tests;

/// <summary>
/// Enforces the dependency direction required by docs/开发指导.md §5: Domain is the innermost layer and
/// must not reference any other project in this solution. Without a test, this rule survives only as long
/// as everyone remembers it.
/// </summary>
[TestClass]
public class ArchitectureTests
{
    [TestMethod]
    public void Domain_depends_on_no_other_project_in_the_solution()
    {
        var assembly = typeof(DomainException).Assembly;

        var projectReferences = assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => name.StartsWith("DailyMusings", StringComparison.Ordinal))
            .ToArray();

        Assert.AreEqual(
            0,
            projectReferences.Length,
            "Domain must not reference other DailyMusings projects, but references: " +
            string.Join(", ", projectReferences));
    }

    [TestMethod]
    public void Domain_carries_no_persistence_or_hosting_dependency()
    {
        var assembly = typeof(DomainException).Assembly;

        var forbidden = assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name =>
                name.StartsWith("Microsoft.Data.Sqlite", StringComparison.Ordinal) ||
                name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) ||
                name.StartsWith("Microsoft.Extensions", StringComparison.Ordinal))
            .ToArray();

        Assert.AreEqual(
            0,
            forbidden.Length,
            "Domain must stay free of infrastructure and hosting concerns, but references: " +
            string.Join(", ", forbidden));
    }
}
