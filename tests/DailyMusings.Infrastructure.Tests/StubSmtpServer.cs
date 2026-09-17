using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using DailyMusings.Application.Abstractions;

namespace DailyMusings.Infrastructure.Tests;

/// <summary>
/// A throwaway SMTP server for one test: it listens on a loopback port, speaks just enough of the protocol to accept
/// one message, and records the whole conversation.
/// <para>
/// Written by hand rather than mocked, because the point of these tests is the wire: that a Chinese subject arrives
/// as one well-formed encoded word, that credentials are not sent before the encryption is up, that the implicit-TLS
/// case really does handshake before the greeting. A stub at the interface level would assert none of that.
/// </para>
/// <para>
/// The certificate is generated per run and the client is told to trust that one explicitly, so nothing here weakens
/// the validation production uses.
/// </para>
/// </summary>
internal sealed class StubSmtpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _serve;
    private readonly List<string> _conversation = [];

    private StubSmtpServer(SmtpSecurity security, string[] capabilities, int authCode)
    {
        Security = security;
        Capabilities = capabilities;
        AuthCode = authCode;

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();

        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        // A certificate is needed whenever this server will ever handshake, which is not the same question as how the
        // connection starts: the STARTTLS case begins in the clear and upgrades later.
        if (security == SmtpSecurity.ImplicitTls ||
            capabilities.Contains("STARTTLS", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                Certificate = SelfSigned();
            }
            catch (Exception exception) when (exception is CryptographicException or NotSupportedException)
            {
                // Named rather than left as "access denied": serving TLS from a test process needs a private key the
                // platform's TLS stack can reach, which on Windows means a write to the user's key store. An
                // environment that forbids that (a locked-down sandbox, a service account without a profile) cannot
                // run these tests at all, and the message should say so instead of looking like a transport bug.
                throw new InvalidOperationException(
                    "This environment cannot create a TLS server certificate, so the encrypted-transport tests cannot "
                    + "run here. On Windows the key has to be written to the user's key store; the suite runs fully on "
                    + "Linux in the SDK container.",
                    exception);
            }
        }

        _serve = Task.Run(ServeAsync);
    }

    /// <summary>A relay that greets in the clear: what a sink on localhost, or a 587 relay, looks like.</summary>
    public static StubSmtpServer Plain(string[]? capabilities = null, int authCode = 235) =>
        new(SmtpSecurity.None, capabilities ?? [], authCode);

    /// <summary>A relay that requires the handshake first: port 465 as the providers run it.</summary>
    public static StubSmtpServer ImplicitTls(string[]? capabilities = null, int authCode = 235) =>
        new(SmtpSecurity.ImplicitTls, capabilities ?? [], authCode);

    public SmtpSecurity Security { get; }

    public int Port { get; }

    /// <summary>Every line the client sent, including the base64 payload lines of an AUTH exchange.</summary>
    public IReadOnlyList<string> Conversation
    {
        get
        {
            lock (_conversation)
            {
                return _conversation.ToArray();
            }
        }
    }

    /// <summary>Everything that arrived between DATA and the terminating dot.</summary>
    public string Message { get; private set; } = string.Empty;

    /// <summary>Whether the body was closed with the "." line the protocol requires.</summary>
    public bool TerminatorReceived { get; private set; }

    /// <summary>
    /// True when the certificate a client presented is this server's own. The test's validation callback uses it, so
    /// the handshake still has to present the right certificate: a transport that skipped the handshake, or connected
    /// somewhere else, cannot pass by accident.
    /// </summary>
    public bool PresentedItsOwnCertificate(X509Certificate? certificate) =>
        certificate is not null && certificate.GetRawCertData().AsSpan().SequenceEqual(_publicCertificate);

    private X509Certificate2? Certificate { get; }

    private byte[] _publicCertificate = [];

    private string Password { get; } = Guid.NewGuid().ToString("N");

    private string[] Capabilities { get; }

    private int AuthCode { get; }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        _listener.Stop();

        try
        {
            await _serve.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException or IOException)
        {
            // The test is over; a listener torn down mid-accept is not a failure.
        }

        _shutdown.Dispose();
        Certificate?.Dispose();
    }

    private async Task ServeAsync()
    {
        using var client = await _listener.AcceptTcpClientAsync(_shutdown.Token).ConfigureAwait(false);

        var stream = (Stream)client.GetStream();

        try
        {
            if (Security == SmtpSecurity.ImplicitTls)
            {
                var secure = new SslStream(stream, leaveInnerStreamOpen: false);
                await secure.AuthenticateAsServerAsync(Certificate!, false, SslProtocols.None, false).ConfigureAwait(false);
                stream = secure;
            }

            var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, leaveOpen: true);
            var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\r\n",
            };

            await writer.WriteLineAsync("220 stub.test ESMTP ready").ConfigureAwait(false);

            var authenticated = false;
            var awaitingUsername = false;
            var awaitingPassword = false;

            while (true)
            {
                var line = await reader.ReadLineAsync(_shutdown.Token).ConfigureAwait(false);

                if (line is null)
                {
                    return;
                }

                lock (_conversation)
                {
                    _conversation.Add(line);
                }

                if (awaitingUsername)
                {
                    awaitingUsername = false;
                    awaitingPassword = true;
                    await writer.WriteLineAsync("334 UGFzc3dvcmQ6").ConfigureAwait(false);
                    continue;
                }

                if (awaitingPassword)
                {
                    awaitingPassword = false;
                    authenticated = AuthCode == 235;
                    await writer.WriteLineAsync($"{AuthCode} stub authentication").ConfigureAwait(false);
                    continue;
                }

                var command = line.ToUpperInvariant();

                if (command.StartsWith("EHLO", StringComparison.Ordinal))
                {
                    var greeting = new StringBuilder("250-stub.test\r\n");

                    foreach (var capability in Capabilities)
                    {
                        greeting.Append("250-").Append(capability).Append("\r\n");
                    }

                    greeting.Append("250 OK");
                    await writer.WriteAsync(greeting + "\r\n").ConfigureAwait(false);
                    continue;
                }

                if (command.StartsWith("HELO", StringComparison.Ordinal))
                {
                    await writer.WriteLineAsync("250 stub.test").ConfigureAwait(false);
                    continue;
                }

                if (command.StartsWith("STARTTLS", StringComparison.Ordinal))
                {
                    await writer.WriteLineAsync("220 Ready to start TLS").ConfigureAwait(false);

                    var secure = new SslStream(stream, leaveInnerStreamOpen: false);
                    await secure.AuthenticateAsServerAsync(Certificate!, false, SslProtocols.None, false).ConfigureAwait(false);

                    // The plaintext reader and writer are abandoned with the upgrade, as RFC 3207 requires.
                    stream = secure;
                    reader = new StreamReader(secure, Encoding.UTF8, false, 1024, leaveOpen: true);
                    writer = new StreamWriter(secure, new UTF8Encoding(false), 1024, leaveOpen: true)
                    {
                        AutoFlush = true,
                        NewLine = "\r\n",
                    };
                    continue;
                }

                if (command.StartsWith("AUTH LOGIN", StringComparison.Ordinal))
                {
                    awaitingUsername = true;
                    await writer.WriteLineAsync("334 VXNlcm5hbWU6").ConfigureAwait(false);
                    continue;
                }

                if (command.StartsWith("AUTH PLAIN", StringComparison.Ordinal))
                {
                    authenticated = AuthCode == 235;
                    await writer.WriteLineAsync($"{AuthCode} stub authentication").ConfigureAwait(false);
                    continue;
                }

                if (command.StartsWith("MAIL FROM", StringComparison.Ordinal) ||
                    command.StartsWith("RCPT TO", StringComparison.Ordinal))
                {
                    await writer.WriteLineAsync("250 OK").ConfigureAwait(false);
                    continue;
                }

                if (command.StartsWith("DATA", StringComparison.Ordinal))
                {
                    await writer.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>").ConfigureAwait(false);

                    var message = new StringBuilder();

                    while (true)
                    {
                        var dataLine = await reader.ReadLineAsync(_shutdown.Token).ConfigureAwait(false);

                        if (dataLine is null)
                        {
                            break;
                        }

                        if (dataLine == ".")
                        {
                            // The end-of-data marker is a line of its own, and it never reaches the recorded
                            // conversation: it belongs to the body's framing, not to the command sequence.
                            TerminatorReceived = true;
                            break;
                        }

                        message.Append(dataLine).Append("\r\n");
                    }

                    Message = message.ToString();
                    await writer.WriteLineAsync("250 OK queued").ConfigureAwait(false);
                    continue;
                }

                if (command.StartsWith("QUIT", StringComparison.Ordinal))
                {
                    await writer.WriteLineAsync("221 Bye").ConfigureAwait(false);
                    return;
                }

                await writer.WriteLineAsync(authenticated ? "250 OK" : "502 Not implemented").ConfigureAwait(false);
            }
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>A certificate for this run only, with the private key a handshake needs.</summary>
    private X509Certificate2 SelfSigned()
    {
        using var key = RSA.Create(2048);
        var password = Guid.NewGuid().ToString("N");

        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));

        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());

        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));

        // The DER form, for the client to compare what it was shown against.
        _publicCertificate = created.Export(X509ContentType.Cert);

        // Round-tripped through PKCS#12 so the private key is attached in the form SslStream wants. Not EphemeralKeySet:
        // on Windows, serving TLS needs a key SChannel can reach, and an ephemeral one fails the handshake with
        // "no credentials are available in the security package". That means this write goes to the user's key store,
        // which is why the TLS tests cannot run in a sandbox that forbids writes outside the project directory.
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pkcs12, password), password);
    }
}
