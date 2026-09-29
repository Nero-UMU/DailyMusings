using System.Net;
using DailyMusings.Domain.Identity;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Api.IntegrationTests;

/// <summary>
/// The deployment-facing half of the first-start bootstrap: an operator who writes one Compose file can state the
/// initial administrator password there instead of digging it out of the container log — and a value that does not
/// clear the product's own password rule is reported rather than silently obeyed or silently dropped.
/// </summary>
[TestClass]
[DoNotParallelize]
public class DeploymentBootstrapTests
{
    [TestMethod]
    public async Task A_configured_initial_password_is_used_and_never_echoed_into_the_container_output()
    {
        const string Configured = "ConfiguredPassword1";

        await using var instance = await TestInstance.StartAsync(
            new Dictionary<string, string?> { ["Admin:InitialPassword"] = Configured });

        Assert.IsFalse(
            instance.BootstrapOutput.Contains(Configured, StringComparison.Ordinal),
            "容器日志会被长期保存，部署配置里的密码不该在里面出现第二遍。");
        Assert.IsFalse(
            instance.BootstrapOutput.Contains("INITIAL-ADMIN-PASSWORD=", StringComparison.Ordinal),
            "没有生成任何密码，也就没有那一行一次性的输出。");
        StringAssert.Contains(
            instance.BootstrapOutput,
            "部署配置",
            "要告诉操作者这串密码来自部署配置，而不是让他去找一行不存在的输出。");

        using var signIn = await instance.SignInAsync(AdminAccount.InitialUsername, Configured);

        Assert.AreEqual(HttpStatusCode.Found, signIn.StatusCode, "部署配置里的那串就是能登录的密码。");
    }

    [TestMethod]
    public async Task An_unusable_configured_initial_password_is_reported_and_still_leaves_a_usable_account()
    {
        await using var instance = await TestInstance.StartAsync(
            new Dictionary<string, string?> { ["Admin:InitialPassword"] = "short" });

        StringAssert.Contains(
            instance.BootstrapOutput,
            "部署配置里的初始管理员密码没有采用",
            "不符合规则的配置值必须被明确说出来，而不是静默忽略。");

        // The fallback behaves exactly like an unconfigured instance — reading the one-time line also asserts that
        // it is still there, which is what keeps a fresh instance reachable.
        using var signIn = await instance.SignInAsync(AdminAccount.InitialUsername, instance.InitialAdminPassword);

        Assert.AreEqual(HttpStatusCode.Found, signIn.StatusCode);
    }
}
