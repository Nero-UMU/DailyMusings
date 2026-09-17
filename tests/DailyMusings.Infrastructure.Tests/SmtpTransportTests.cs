using System.Net;
using System.Net.Mail;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using DailyMusings.Application.Abstractions;
using DailyMusings.Infrastructure.Notifications;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Infrastructure.Tests;

/// <summary>
/// The SMTP transport, on the wire (docs/开发指导.md §12).
/// <para>
/// These exist because the transport is written by hand: the framework's client could do 587 but not the implicit TLS
/// that 465 needs, so "copy the settings out of my other program" had to keep working without it. Everything a
/// hand-written client could get wrong is asserted here against a stub server — the order of the handshake and the
/// credentials, the shape of a Chinese subject, the refusal to send a password in the clear, and which failures the
/// job should retry (§14).
/// </para>
/// </summary>
[TestClass]
public class SmtpTransportTests
{
    private const string FromAddress = "dailymusings@example.test";
    private const string ToAddress = "owner@example.test";

    [TestMethod]
    public async Task A_message_reaches_a_plain_relay_in_the_shape_a_mail_server_expects()
    {
        await using var server = StubSmtpServer.Plain();
        var message = new EmailMessage(ToAddress, "2026-09-17 的草稿已就绪", "今天有 3 条记录。\r\n草稿已生成。");

        await new SmtpMailTransport(Settings(server, SmtpSecurity.None), password: null)
            .SendAsync(message, CancellationToken.None);

        Assert.AreEqual(
            string.Join(" | ", "EHLO 127.0.0.1", $"MAIL FROM:<{FromAddress}>", $"RCPT TO:<{ToAddress}>", "DATA", "QUIT"),
            string.Join(" | ", server.Conversation),
            "The command sequence a mail server expects, in order.");

        Assert.IsTrue(server.TerminatorReceived, "The body has to be closed with a line containing only a dot.");

        StringAssert.Contains(server.Message, $"To: <{ToAddress}>");
        Assert.AreEqual("2026-09-17 的草稿已就绪", Decode(Header(server.Message, "Subject")));

        var from = Header(server.Message, "From");
        var angle = from.IndexOf('<', StringComparison.Ordinal);

        Assert.IsTrue(angle > 0, $"From has to keep the display name and the address: {from}");
        Assert.AreEqual("每日随想", Decode(from[..angle].Trim()));
        Assert.AreEqual(FromAddress, from[(angle + 1)..].Trim('<', '>'));

        Assert.AreEqual(message.Body, DecodeBody(server.Message), "The body has to arrive as the text the composer wrote.");
        StringAssert.Contains(server.Message, "Content-Type: text/plain; charset=utf-8");
    }

    [TestMethod]
    public async Task Implicit_tls_hands_over_the_greeting_inside_the_tunnel()
    {
        // The stub authenticates before it writes anything, so a conversation at all means the client handshook first
        // — which is the whole difference between port 465 and port 587.
        await using var server = StubSmtpServer.ImplicitTls(["AUTH PLAIN LOGIN"]);

        await Send(server, SmtpSecurity.ImplicitTls, "s3cret-from-implicit");

        StringAssert.StartsWith(server.Conversation[0], "EHLO");
        StringAssert.Contains(server.Message, "Subject:", "The message has to arrive inside the tunnel.");
        Assert.IsTrue(
            server.Conversation.Any(line => line.StartsWith("AUTH PLAIN ", StringComparison.Ordinal)),
            "An implicit-TLS relay that advertises AUTH PLAIN should have been authenticated against.");
    }

    [TestMethod]
    public async Task Starttls_upgrades_before_anything_secret_is_offered()
    {
        await using var server = StubSmtpServer.Plain(["STARTTLS", "AUTH LOGIN PLAIN"]);

        await Send(server, SmtpSecurity.StartTls, "s3cret-from-starttls");

        var conversation = server.Conversation.ToList();
        var startTls = conversation.IndexOf("STARTTLS");
        var auth = conversation.FindIndex(line => line.StartsWith("AUTH", StringComparison.Ordinal));
        var mail = conversation.FindIndex(line => line.StartsWith("MAIL FROM", StringComparison.Ordinal));

        Assert.IsTrue(startTls > 0, "STARTTLS has to be issued after the first EHLO.");
        Assert.AreEqual(
            startTls + 1,
            conversation.IndexOf("EHLO 127.0.0.1", startTls),
            "RFC 3207: EHLO is repeated inside the tunnel, because everything said before it is void.");
        Assert.IsTrue(auth > startTls, "Credentials may not be offered before the upgrade — that is what STARTTLS is for.");
        Assert.IsTrue(mail > auth);
    }

    [TestMethod]
    public async Task A_certificate_that_does_not_validate_stops_the_send()
    {
        await using var server = StubSmtpServer.ImplicitTls();

        var refusal = await Assert.ThrowsExceptionAsync<PermanentExternalFailureException>(
            () => new SmtpMailTransport(
                    Settings(server, SmtpSecurity.ImplicitTls),
                    password: null,
                    // What the platform's own validation would do with a certificate that is not trusted.
                    trustServer: (_, _, _, _) => false)
                .SendAsync(new EmailMessage(ToAddress, "subject", "body"), CancellationToken.None));

        Assert.AreEqual("notification.smtp_tls_failed", refusal.Code);
        Assert.AreEqual(0, server.Conversation.Count, "Nothing may be sent over a tunnel that was not established.");
    }

    [TestMethod]
    public async Task A_refused_password_is_permanent_and_an_unreachable_relay_is_transient()
    {
        await using var refusing = StubSmtpServer.ImplicitTls(["AUTH PLAIN"], authCode: 535);

        var refusal = await Assert.ThrowsExceptionAsync<PermanentExternalFailureException>(
            () => Send(refusing, SmtpSecurity.ImplicitTls, "wrong-password"));

        Assert.AreEqual("notification.rejected", refusal.Code, "A refused password will be refused again (§14).");

        // A port nothing is listening on: the failure an operator sees when the relay is down, which is worth retrying.
        var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        var closedPort = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();

        var settings = SmtpSettings.Default with
        {
            Enabled = true,
            Host = "127.0.0.1",
            Port = closedPort,
            Security = SmtpSecurity.None,
            FromAddress = FromAddress,
            Timeout = TimeSpan.FromSeconds(5),
        };

        var unreachable = await Assert.ThrowsExceptionAsync<TransientExternalFailureException>(
            () => new SmtpMailTransport(settings, null)
                .SendAsync(new EmailMessage(ToAddress, "subject", "body"), CancellationToken.None));

        Assert.AreEqual("notification.smtp_failed", unreachable.Code);
    }

    [TestMethod]
    public async Task Credentials_are_never_sent_over_a_connection_that_is_not_encrypted()
    {
        await using var server = StubSmtpServer.Plain(["AUTH PLAIN LOGIN"]);

        var refusal = await Assert.ThrowsExceptionAsync<PermanentExternalFailureException>(
            () => Send(server, SmtpSecurity.None, "s3cret-in-the-clear"));

        Assert.AreEqual("notification.insecure_credentials", refusal.Code);
        Assert.IsFalse(
            server.Conversation.Any(line => line.Contains("AUTH", StringComparison.Ordinal)),
            "Nothing may be offered to a relay this instance would have to send a password to in the clear.");
        Assert.IsFalse(
            string.Join("\n", server.Conversation).Contains("s3cret-in-the-clear", StringComparison.Ordinal),
            "The password must not appear on the wire at all.");
    }

    [TestMethod]
    public async Task A_relay_that_cannot_starttls_is_refused_rather_than_quietly_downgraded()
    {
        await using var server = StubSmtpServer.Plain();

        var refusal = await Assert.ThrowsExceptionAsync<PermanentExternalFailureException>(
            () => Send(server, SmtpSecurity.StartTls, "s3cret"));

        Assert.AreEqual("notification.starttls_unsupported", refusal.Code);
        Assert.IsFalse(server.Conversation.Any(line => line.StartsWith("MAIL FROM", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task A_header_value_with_a_line_break_is_refused_before_anything_is_sent()
    {
        await using var server = StubSmtpServer.Plain();

        var injected = new EmailMessage(ToAddress, "草稿已就绪\r\nBcc: attacker@example.test", "body");

        var refusal = await Assert.ThrowsExceptionAsync<PermanentExternalFailureException>(
            () => new SmtpMailTransport(Settings(server, SmtpSecurity.None), null)
                .SendAsync(injected, CancellationToken.None));

        Assert.AreEqual("notification.invalid_message", refusal.Code);
        Assert.AreEqual(0, server.Conversation.Count, "A header injection is caught before the connection is used.");
    }

    [TestMethod]
    public async Task A_long_chinese_subject_is_split_into_encoded_words_that_still_decode()
    {
        await using var server = StubSmtpServer.Plain();
        var subject = string.Concat(Enumerable.Repeat("每日随想 · ", 12)).TrimEnd();

        await new SmtpMailTransport(Settings(server, SmtpSecurity.None), null)
            .SendAsync(new EmailMessage(ToAddress, subject, "body"), CancellationToken.None);

        var header = Header(server.Message, "Subject");
        var words = Regex.Matches(header, @"=\?utf-8\?B\?[^?]*\?=", RegexOptions.IgnoreCase);

        Assert.IsTrue(words.Count > 1, $"A subject this long cannot be one encoded word: {header}");
        Assert.IsTrue(words.All(word => word.Length <= 75), $"Every encoded word must stay under 75 characters: {header}");
        Assert.AreEqual(subject, Decode(header), "Splitting must not corrupt a multi-byte character.");
    }

    [TestMethod]
    public void The_header_encoding_agrees_with_the_framework_on_the_same_name()
    {
        // The framework's own encoder is the oracle for the bytes: a recipient's client has to show the same name
        // whether a message came from this transport or from anything else built on .NET. The name is taken apart the
        // way a client would — strip whatever quoting the framework adds, then decode the encoded word — so this pins
        // the encoding, not .NET's formatting habits.
        var expected = new MailAddress(FromAddress, "每日随想", Encoding.UTF8).ToString();
        var name = expected[..expected.IndexOf('<', StringComparison.Ordinal)].Trim().Trim('"');

        Assert.AreEqual("每日随想", Decode(name));
    }

    private static Task Send(StubSmtpServer server, SmtpSecurity security, string password) =>
        new SmtpMailTransport(Settings(server, security, username: FromAddress), password, Trust(server))
            .SendAsync(new EmailMessage(ToAddress, "subject", "body"), CancellationToken.None);

    /// <summary>
    /// Trusts the stub's own certificate and nothing else: the handshake still has to present the right one, so a
    /// transport that skipped it, or connected somewhere else, cannot pass by accident.
    /// </summary>
    private static RemoteCertificateValidationCallback Trust(StubSmtpServer server) =>
        (_, certificate, _, _) => server.PresentedItsOwnCertificate(certificate);

    private static SmtpSettings Settings(StubSmtpServer server, SmtpSecurity security, string? username = null) =>
        SmtpSettings.Default with
        {
            Enabled = true,
            Host = "127.0.0.1",
            Port = server.Port,
            Security = security,
            Username = username,
            FromAddress = FromAddress,
            FromName = "每日随想",
            Timeout = TimeSpan.FromSeconds(15),
        };

    /// <summary>One header's value, unfolded, from the message the stub received.</summary>
    private static string Header(string message, string name)
    {
        var headers = message[..message.IndexOf("\r\n\r\n", StringComparison.Ordinal)];

        foreach (var line in headers.Replace("\r\n ", " ", StringComparison.Ordinal).Split("\r\n"))
        {
            if (line.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))
            {
                return line[(name.Length + 1)..].Trim();
            }
        }

        Assert.Fail($"The message has no {name} header:\n{headers}");
        return string.Empty;
    }

    /// <summary>RFC 2047 encoded words back to text, ignoring the folding whitespace between them.</summary>
    private static string Decode(string value)
    {
        var matches = Regex.Matches(value, @"=\?utf-8\?B\?(?<data>[^?]*)\?=", RegexOptions.IgnoreCase);

        if (matches.Count == 0)
        {
            return value;
        }

        var text = new StringBuilder();

        foreach (Match word in matches)
        {
            text.Append(Encoding.UTF8.GetString(Convert.FromBase64String(word.Groups["data"].Value)));
        }

        return text.ToString();
    }

    private static string DecodeBody(string message) =>
        Encoding.UTF8.GetString(
            Convert.FromBase64String(
                message[(message.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..]
                    .Replace("\r\n", string.Empty, StringComparison.Ordinal)));
}
