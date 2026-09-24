using DailyMusings.Application.Abstractions;
using DailyMusings.Domain.Common;
using DailyMusings.Domain.Publishing;
using DailyMusings.Infrastructure.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Infrastructure.Tests;

/// <summary>
/// The "test connection" button for SMTP (docs/开发指导.md §16).
/// <para>
/// It reads a greeting and nothing else, which is why it needs its own coverage now that a relay can be reached two
/// ways: a probe written for the plaintext case reports a 465 configuration as an unreachable server, and the
/// operator is told their working mailbox is broken. A mismatch the other way round — the SSL checkbox ticked against
/// a relay that speaks in the clear — has to come back as a TLS failure rather than as fifteen seconds of silence.
/// A 587 relay needs no special handling: it greets in the clear by definition.
/// </para>
/// <para>
/// The unused dependencies are stubs that throw: this probe only ever touches the SMTP settings, so anything else
/// being called would be a bug worth failing on.
/// </para>
/// </summary>
[TestClass]
public class SmtpProbeTests
{
    [TestMethod]
    public async Task An_implicit_tls_relay_is_probed_inside_the_tunnel()
    {
        await using var server = StubSmtpServer.ImplicitTls();

        var result = await ProbeAsync(server, useSsl: true);

        Assert.IsTrue(result.Ok, $"{result.Code}: {result.Detail}");
        StringAssert.Contains(result.Detail!, "220", "The probe reports the greeting it actually read.");
    }

    [TestMethod]
    public async Task A_plain_relay_is_still_probed_the_way_it_always_was()
    {
        await using var server = StubSmtpServer.Plain();

        var result = await ProbeAsync(server, useSsl: false);

        Assert.IsTrue(result.Ok, $"{result.Code}: {result.Detail}");
    }

    /// <summary>
    /// A 587 relay greets in the clear and only upgrades when asked, so STARTTLS changes nothing about reading the
    /// greeting — the probe must not try to handshake before it, or a working 587 configuration would hang.
    /// </summary>
    [TestMethod]
    public async Task A_starttls_relay_is_probed_in_the_clear_because_that_is_where_its_greeting_is()
    {
        await using var server = StubSmtpServer.Plain(["STARTTLS"]);

        var result = await ProbeAsync(server, useSsl: false);

        Assert.IsTrue(result.Ok, $"{result.Code}: {result.Detail}");
        Assert.IsFalse(
            server.Conversation.Any(line => line.StartsWith("STARTTLS", StringComparison.Ordinal)),
            "Reading a greeting is not a send: the probe does not upgrade, and has no reason to.");
    }

    [TestMethod]
    public async Task Asking_for_a_tunnel_from_a_relay_that_has_none_is_a_tls_failure_not_a_silent_timeout()
    {
        await using var server = StubSmtpServer.Plain();

        var result = await ProbeAsync(server, useSsl: true);

        Assert.IsFalse(result.Ok);
        Assert.AreEqual(
            "probe.smtp.tls_failed",
            result.Code,
            "A plaintext greeting is not TLS, and saying so is what tells the operator the port is wrong.");
    }

    private static async Task<ProbeResult> ProbeAsync(StubSmtpServer server, bool useSsl)
    {
        var settings = SmtpSettings.Default with
        {
            Enabled = true,
            Host = "127.0.0.1",
            Port = server.Port,
            FromAddress = "dailymusings@example.test",
            UseSsl = useSsl,
            UseStartTls = false,
        };

        var probe = new ExternalServiceProbe(
            new HttpClient(),
            new UnusedSecretStore(),
            new UnusedTranscriptionSettings(),
            new UnusedGenerationSettings(),
            new UnusedEmbeddingSettings(),
            new TestSmtpSettings { Settings = settings },
            NullLogger<ExternalServiceProbe>.Instance,

            // The probe validates certificates on purpose, so a throwaway one has to be trusted explicitly — which is
            // also the seam a relay behind a private CA would use. Trusting the stub's own certificate keeps the
            // handshake honest: a probe that skipped it would present nothing to compare.
            trustServer: (_, certificate, _, _) => server.PresentedItsOwnCertificate(certificate));

        return await probe.ProbeAsync(ExternalService.Smtp, CancellationToken.None);
    }

    private sealed class UnusedSecretStore : ISecretStore
    {
        public string? TryGet(string name) => throw new NotSupportedException(nameof(UnusedSecretStore));

        public bool Exists(string name) => throw new NotSupportedException(nameof(UnusedSecretStore));

        public IReadOnlyList<string> ListNames() => throw new NotSupportedException(nameof(UnusedSecretStore));

        public SecretSource ResolveSource(string name) => throw new NotSupportedException(nameof(UnusedSecretStore));
    }

    private sealed class UnusedTranscriptionSettings : ITranscriptionSettingsProvider
    {
        public Task<TranscriptionSettings> GetAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException(nameof(UnusedTranscriptionSettings));
    }

    private sealed class UnusedGenerationSettings : IGenerationSettingsProvider
    {
        public Task<GenerationSettings> GetAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException(nameof(UnusedGenerationSettings));
    }

    private sealed class UnusedEmbeddingSettings : IEmbeddingSettingsProvider
    {
        public Task<EmbeddingSettings> GetAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException(nameof(UnusedEmbeddingSettings));
    }
}
