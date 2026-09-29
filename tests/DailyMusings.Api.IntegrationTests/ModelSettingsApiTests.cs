using System.Net;
using System.Net.Http.Json;
using DailyMusings.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Api.IntegrationTests;

/// <summary>
/// §8.1's admin-side configuration over the real API, including who may see what.
/// <para>
/// The split under test is the one that matters to a phone: a client may learn the model <em>names</em> and whether
/// they are on, and nothing about an instance's endpoints or where its credentials are filed. The administrator's
/// form is the only place the Base URL and the secret's name appear.
/// </para>
/// </summary>
[TestClass]
[DoNotParallelize]
public class ModelSettingsApiTests
{
    [TestMethod]
    public async Task An_administrator_can_configure_a_model_endpoint_and_read_it_back()
    {
        await using var instance = await TestInstance.StartAsync();
        await instance.SignInAsChangedAdministratorAsync();

        using var updated = await instance.Client.PatchAsJsonAsync(
            "/api/system/model-endpoints/embedding",
            new UpdateModelEndpointRequest(
                Enabled: true,
                BaseUrl: "https://api.example.com/v1/",
                Model: "text-embedding-3-small",
                SecretName: "embedding-api-key",
                TimeoutSeconds: 45,
                Dimensions: 512));

        updated.EnsureSuccessStatusCode();
        var endpoint = await updated.Content.ReadFromJsonAsync<ModelEndpointDto>();

        Assert.IsNotNull(endpoint);
        Assert.AreEqual("embedding", endpoint.Service);
        Assert.IsTrue(endpoint.Enabled);
        Assert.AreEqual("https://api.example.com/v1", endpoint.BaseUrl, "The trailing slash is stripped by the use case.");
        Assert.AreEqual(512, endpoint.Dimensions);

        var all = await instance.Client.GetFromJsonAsync<ModelEndpointListResponse>("/api/system/model-endpoints");
        Assert.AreEqual(3, all!.Items.Count);
        Assert.AreEqual("text-embedding-3-small", all.Items.Single(item => item.Service == "embedding").Model);
    }

    [TestMethod]
    public async Task A_value_that_would_fail_later_is_refused_with_a_stable_code()
    {
        await using var instance = await TestInstance.StartAsync();
        await instance.SignInAsChangedAdministratorAsync();

        using var response = await instance.Client.PatchAsJsonAsync(
            "/api/system/model-endpoints/generation",
            new UpdateModelEndpointRequest(true, "not-a-url", null, null, null, null));

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.AreEqual("model.baseUrl.invalid", error!.Code);
    }

    [TestMethod]
    public async Task Transcription_protocol_parameters_round_trip_through_the_admin_API()
    {
        await using var instance = await TestInstance.StartAsync();
        await instance.SignInAsChangedAdministratorAsync();
        var parameters = new TranscriptionParametersDto(
            "dashscope-multimodal",
            "zh,en",
            EnableItn: false,
            VocabularyId: "vocabulary-42",
            SpeakerDiarization: true,
            KeepDialect: true);

        using var updated = await instance.Client.PatchAsJsonAsync(
            "/api/system/model-endpoints/transcription",
            new UpdateModelEndpointRequest(
                true,
                "https://workspace.example/compatible-mode/v1",
                "qwen-audio-3.1-asr-flash",
                "openai-api-key",
                120,
                null,
                Transcription: parameters));

        updated.EnsureSuccessStatusCode();
        var endpoint = await updated.Content.ReadFromJsonAsync<ModelEndpointDto>();

        Assert.IsNotNull(endpoint?.Transcription);
        Assert.AreEqual(parameters, endpoint.Transcription);
    }

    [TestMethod]
    public async Task A_paired_device_may_see_the_model_names_and_nothing_else()
    {
        await using var instance = await TestInstance.StartAsync();
        await instance.SignInAsChangedAdministratorAsync();

        await instance.Client.PatchAsJsonAsync(
            "/api/system/model-endpoints/transcription",
            new UpdateModelEndpointRequest(true, "http://127.0.0.1:9/v1", "whisper-local", "openai-api-key", 60, null));

        var (_, device) = await instance.PairDeviceAsync();

        var names = await device.GetFromJsonAsync<ModelNameListResponse>("/api/system/models");
        Assert.IsNotNull(names);
        Assert.AreEqual(3, names.Items.Count);
        Assert.AreEqual("whisper-local", names.Items.Single(item => item.Service == "transcription").Model);
        Assert.IsTrue(names.Items.Single(item => item.Service == "transcription").Enabled);

        // The name list carries no Base URL and no secret name: the shape itself is the guarantee, so this asserts
        // on the serialized document rather than on a DTO that simply happens not to have the fields.
        var raw = await device.GetStringAsync("/api/system/models");
        Assert.IsFalse(raw.Contains("baseUrl", StringComparison.OrdinalIgnoreCase), raw);
        Assert.IsFalse(raw.Contains("secretName", StringComparison.OrdinalIgnoreCase), raw);
        Assert.IsFalse(raw.Contains("openai-api-key", StringComparison.Ordinal), raw);

        using var adminOnlyRead = await device.GetAsync("/api/system/model-endpoints");
        Assert.IsTrue(
            adminOnlyRead.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"A device must not read the endpoints' URLs and secret names (got {adminOnlyRead.StatusCode}).");

        using var deviceWrite = await device.PatchAsJsonAsync(
            "/api/system/model-endpoints/generation",
            new UpdateModelEndpointRequest(true, "https://attacker.example/v1", null, null, null, null));

        Assert.IsTrue(
            deviceWrite.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"A device must not configure where the instance's material is sent (got {deviceWrite.StatusCode}).");
    }

    [TestMethod]
    public async Task Smtp_settings_are_administrable_and_validated()
    {
        await using var instance = await TestInstance.StartAsync();
        await instance.SignInAsChangedAdministratorAsync();

        using var updated = await instance.Client.PatchAsJsonAsync(
            "/api/system/smtp-settings",
            new UpdateSmtpSettingsRequest(
                Enabled: true,
                Host: "smtp.example.com",
                Port: 587,
                FromAddress: "dailymusings@example.com",
                UseSsl: false,
                UseStartTls: true));

        updated.EnsureSuccessStatusCode();
        var smtp = await updated.Content.ReadFromJsonAsync<SmtpSettingsDto>();

        Assert.IsNotNull(smtp);
        Assert.IsTrue(smtp.Enabled);
        Assert.AreEqual("smtp.example.com", smtp.Host);
        Assert.AreEqual(587, smtp.Port);
        Assert.AreEqual("dailymusings@example.com", smtp.FromAddress);
        Assert.IsTrue(smtp.UseStartTls);
        Assert.IsFalse(smtp.UseSsl);

        // The whole point of the two checkboxes: what the other program's form calls "SSL" has to be expressible here.
        using var implicitTls = await instance.Client.PatchAsJsonAsync(
            "/api/system/smtp-settings",
            new UpdateSmtpSettingsRequest(null, null, 465, null, UseSsl: true, UseStartTls: false));

        implicitTls.EnsureSuccessStatusCode();

        var switched = await implicitTls.Content.ReadFromJsonAsync<SmtpSettingsDto>();
        Assert.IsTrue(switched!.UseSsl);
        Assert.IsFalse(switched.UseStartTls);

        foreach (var (request, code) in new (UpdateSmtpSettingsRequest, string)[]
                 {
                     (new UpdateSmtpSettingsRequest(null, null, 70_000, null, null, null), "smtp.port.out_of_range"),
                     (new UpdateSmtpSettingsRequest(null, null, null, "not-an-address", null, null), "smtp.from_address.invalid"),

                     // The pair no transport can carry out. It used to be an unparseable "security" token that was
                     // rejected here; now the form sends booleans, so the refusal is about the combination.
                     (new UpdateSmtpSettingsRequest(null, null, null, null, UseSsl: true, UseStartTls: true), "smtp.security.conflicting"),
                 })
        {
            using var invalid = await instance.Client.PatchAsJsonAsync("/api/system/smtp-settings", request);

            Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.AreEqual(code, (await invalid.Content.ReadFromJsonAsync<ApiError>())!.Code);
        }
    }
}
