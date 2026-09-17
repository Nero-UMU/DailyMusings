using System.Net;
using System.Net.Http.Json;
using DailyMusings.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Api.IntegrationTests;

/// <summary>
/// §8.1's per-target WordPress site, over the real API.
/// <para>
/// A target's site address and the <em>name</em> of its application-password secret used to exist only in the
/// deployment configuration. They can now be overridden for one target, without a redeploy — and clearing the
/// override has to put the deployment's own answer back, which is the half that is easy to get wrong.
/// </para>
/// </summary>
[TestClass]
[DoNotParallelize]
public class WordPressSiteApiTests
{
    [TestMethod]
    public async Task An_administrator_can_point_one_target_at_another_site_and_clear_it_again()
    {
        await using var instance = await TestInstance.StartAsync();
        await instance.SignInAsChangedAdministratorAsync();

        var path = await CreateTargetAsync(instance, "blog", "wordPress", "blog");

        var before = await instance.Client.GetFromJsonAsync<WordPressSiteDto>(path);

        Assert.IsNotNull(before);
        Assert.IsFalse(before.Overridden, "Nothing has been overridden on a fresh instance.");

        using var updated = await instance.Client.PatchAsJsonAsync(
            path,
            new UpdateWordPressSiteOverrideRequest(
                BaseUrl: "https://overridden.example.test/",
                Username: "override-user",
                SecretName: "wp-secret",
                TimeoutSeconds: 45));

        updated.EnsureSuccessStatusCode();
        var saved = await updated.Content.ReadFromJsonAsync<WordPressSiteDto>();

        Assert.IsNotNull(saved);
        Assert.IsTrue(saved.Overridden);
        Assert.AreEqual("https://overridden.example.test", saved.EffectiveBaseUrl, "The trailing slash is stripped.");
        Assert.AreEqual("override-user", saved.EffectiveUsername);
        Assert.AreEqual("wp-secret", saved.EffectiveSecretName, "Only the secret's name is ever stored or returned.");
        Assert.AreEqual(45, saved.EffectiveTimeoutSeconds);

        // The read path a publication actually takes has to agree with what the write path reported.
        var reread = await instance.Client.GetFromJsonAsync<WordPressSiteDto>(path);
        Assert.AreEqual("https://overridden.example.test", reread!.EffectiveBaseUrl);
        Assert.AreEqual(45, reread.EffectiveTimeoutSeconds);

        // Clearing is one request and puts the deployment configuration back in charge.
        using var cleared = await instance.Client.PatchAsJsonAsync(
            path,
            new UpdateWordPressSiteOverrideRequest(null, null, null, null));

        cleared.EnsureSuccessStatusCode();
        var afterClear = await cleared.Content.ReadFromJsonAsync<WordPressSiteDto>();

        Assert.IsNotNull(afterClear);
        Assert.IsFalse(afterClear.Overridden, "Clearing has to be reported as cleared.");
        Assert.AreNotEqual("https://overridden.example.test", afterClear.EffectiveBaseUrl);
        Assert.AreNotEqual(45, afterClear.EffectiveTimeoutSeconds);
    }

    /// <summary>
    /// A blank field means "stop overriding this one", so a partial override keeps the configured values for the
    /// fields the operator left alone — which is what makes the form usable.
    /// </summary>
    [TestMethod]
    public async Task A_partial_override_leaves_the_other_fields_on_the_deployment_configuration()
    {
        await using var instance = await TestInstance.StartAsync();
        await instance.SignInAsChangedAdministratorAsync();

        var path = await CreateTargetAsync(instance, "blog", "wordPress", "blog");
        var before = await instance.Client.GetFromJsonAsync<WordPressSiteDto>(path);

        using var updated = await instance.Client.PatchAsJsonAsync(
            path,
            new UpdateWordPressSiteOverrideRequest("https://only-the-address.example.test", null, null, null));

        updated.EnsureSuccessStatusCode();
        var saved = await updated.Content.ReadFromJsonAsync<WordPressSiteDto>();

        Assert.IsNotNull(saved);
        Assert.AreEqual("https://only-the-address.example.test", saved.EffectiveBaseUrl);
        Assert.AreEqual(before!.EffectiveUsername, saved.EffectiveUsername, "Untouched fields keep their configured value.");
        Assert.AreEqual(before.EffectiveSecretName, saved.EffectiveSecretName);
        Assert.AreEqual(before.EffectiveTimeoutSeconds, saved.EffectiveTimeoutSeconds);
    }

    [TestMethod]
    public async Task Values_that_could_not_work_are_refused_with_stable_codes()
    {
        await using var instance = await TestInstance.StartAsync();
        await instance.SignInAsChangedAdministratorAsync();

        var path = await CreateTargetAsync(instance, "blog", "wordPress", "blog");

        await AssertRefusedAsync(
            instance,
            path,
            new UpdateWordPressSiteOverrideRequest("not a url", null, null, null),
            "publish.site.base_url.invalid");

        await AssertRefusedAsync(
            instance,
            path,
            new UpdateWordPressSiteOverrideRequest(null, null, "../etc/passwd", null),
            "publish.site.secret_name.invalid");

        await AssertRefusedAsync(
            instance,
            path,
            new UpdateWordPressSiteOverrideRequest(null, null, null, 9_999),
            "publish.site.timeout.invalid");

        // None of the refused writes may have been applied.
        var after = await instance.Client.GetFromJsonAsync<WordPressSiteDto>(path);
        Assert.IsFalse(after!.Overridden);
    }

    [TestMethod]
    public async Task A_markdown_target_has_no_site_to_configure()
    {
        await using var instance = await TestInstance.StartAsync();
        await instance.SignInAsChangedAdministratorAsync();

        var path = await CreateTargetAsync(instance, "drafts", "markdown", "drafts");

        using var response = await instance.Client.GetAsync(path);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreEqual("publish.site.not_wordpress", (await response.Content.ReadFromJsonAsync<ApiError>())!.Code);
    }

    [TestMethod]
    public async Task A_paired_device_cannot_point_a_target_at_another_site()
    {
        await using var instance = await TestInstance.StartAsync();
        await instance.SignInAsChangedAdministratorAsync();

        var path = await CreateTargetAsync(instance, "blog", "wordPress", "blog");
        var (_, device) = await instance.PairDeviceAsync();

        using var read = await device.GetAsync(path);
        Assert.IsTrue(
            read.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"A capture credential must not learn where the blog is (got {read.StatusCode}).");

        using var write = await device.PatchAsJsonAsync(
            path,
            new UpdateWordPressSiteOverrideRequest("https://attacker.example", "attacker", "attacker", null));

        Assert.IsTrue(
            write.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"A device must not redirect the instance's publishing (got {write.StatusCode}).");

        // And nothing changed.
        var after = await instance.Client.GetFromJsonAsync<WordPressSiteDto>(path);
        Assert.IsFalse(after!.Overridden);
    }

    private static async Task<string> CreateTargetAsync(
        TestInstance instance,
        string name,
        string type,
        string? destinationReference)
    {
        using var response = await instance.Client.PostAsJsonAsync(
            "/api/publish-targets",
            new CreatePublishTargetRequest(name, type, destinationReference));

        response.EnsureSuccessStatusCode();

        var target = await response.Content.ReadFromJsonAsync<PublishTargetDto>();

        Assert.IsNotNull(target);
        return $"/api/publish-targets/{target.Id}/wordpress";
    }

    private static async Task AssertRefusedAsync(
        TestInstance instance,
        string path,
        UpdateWordPressSiteOverrideRequest request,
        string expectedCode)
    {
        using var response = await instance.Client.PatchAsJsonAsync(path, request);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, expectedCode);
        Assert.AreEqual(expectedCode, (await response.Content.ReadFromJsonAsync<ApiError>())!.Code);
    }
}
