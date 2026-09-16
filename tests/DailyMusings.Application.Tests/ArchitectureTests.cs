using DailyMusings.Application.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Application.Tests;

/// <summary>
/// docs/开发指导.md §5: Application depends on Domain only. Infrastructure implements the ports it defines,
/// and the hosts compose them — so this layer must not reach outward.
/// </summary>
[TestClass]
public class ArchitectureTests
{
    [TestMethod]
    public void Application_references_only_the_domain_among_this_solutions_projects()
    {
        var assembly = typeof(IClock).Assembly;

        var projectReferences = assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => name.StartsWith("DailyMusings", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(
            new[] { "DailyMusings.Domain" },
            projectReferences,
            "Application may reference Domain and nothing else: " + string.Join(", ", projectReferences));
    }

    [TestMethod]
    public void Application_carries_no_persistence_or_hosting_dependency()
    {
        var assembly = typeof(IClock).Assembly;

        var forbidden = assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name =>
                name.StartsWith("Microsoft.Data.Sqlite", StringComparison.Ordinal) ||
                name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal))
            .ToArray();

        Assert.AreEqual(
            0,
            forbidden.Length,
            "Application must stay free of storage and hosting concerns, but references: " +
            string.Join(", ", forbidden));
    }
}
