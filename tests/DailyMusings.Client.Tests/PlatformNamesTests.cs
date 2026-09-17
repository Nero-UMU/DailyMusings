using DailyMusings.Client.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Client.Tests;

/// <summary>
/// One small mapping, tested because the alternative is what happened: the platform a client reported to the server
/// was the literal "android" in every build, and the first Windows client therefore registered itself in the device
/// list as an Android phone (docs/开发指导.md §10.2).
/// </summary>
[TestClass]
public class PlatformNamesTests
{
    [TestMethod]
    public void The_runtime_platform_names_map_onto_the_wire_names()
    {
        Assert.AreEqual("android", PlatformNames.FromRuntimeName("Android"));
        Assert.AreEqual("windows", PlatformNames.FromRuntimeName("WinUI"));
        Assert.AreEqual("windows", PlatformNames.FromRuntimeName("windows"));
        Assert.AreEqual("ios", PlatformNames.FromRuntimeName("iOS"));
        Assert.AreEqual("macos", PlatformNames.FromRuntimeName("MacCatalyst"));
        Assert.AreEqual("macos", PlatformNames.FromRuntimeName("MacOS"));
    }

    [TestMethod]
    public void An_unknown_platform_is_reported_as_unknown_rather_than_guessed_at()
    {
        Assert.AreEqual("unknown", PlatformNames.FromRuntimeName(null));
        Assert.AreEqual("unknown", PlatformNames.FromRuntimeName(""));
        Assert.AreEqual("unknown", PlatformNames.FromRuntimeName("  "));
        Assert.AreEqual("unknown", PlatformNames.FromRuntimeName("Tizen"));
    }
}
