using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using DailyMusings.Application.Abstractions;

namespace DailyMusings.Infrastructure.Notifications;

/// <summary>
/// Sends one message over SMTP (docs/开发指导.md §12), speaking the two ways a relay can be protected.
/// <para>
/// Why this exists at all: the framework's <c>System.Net.Mail.SmtpClient</c> implements <em>explicit</em> TLS only.
/// It can do STARTTLS on 587, and it cannot do implicit TLS on 465 — the "SSL: true, STARTTLS: false" that almost
/// every other mail form shows — because that requires the handshake to happen before the greeting. An operator
/// copying their provider's settings out of another program would have been told their mailbox does not work, which
/// is a defect in this product, not in their mailbox.
/// </para>
/// <para>
/// Why it is written here rather than taken from a library: this instance sends a handful of short plain-text
/// notifications to one recipient, and the whole protocol it needs is the hundred lines below. A client library would
/// bring a dependency into every deployment (including the offline ones) to cover features nothing here calls, and
/// the project's rule is not to add machinery before it is needed (§20).
/// </para>
/// <para>
/// What it deliberately does not do: HTML or attachments, several recipients, BCC, DKIM, pipelining, or sending
/// credentials over an unencrypted connection — the last one is refused outright rather than quietly attempted.
/// </para>
/// </summary>
public sealed class SmtpMailTransport
{
    /// <summary>Base64 body lines. RFC 5322 asks for at most 78 characters per line.</summary>
    private const int MaximumLineLength = 76;

    /// <summary>45 bytes of UTF-8 become exactly 60 base64 characters, leaving room for the "=?utf-8?B?" wrapper.</summary>
    private const int EncodedWordPayloadBytes = 45;

    /// <summary>How much of a server's own words may reach a log line or a failure record (§16).</summary>
    private const int MaximumQuotedReply = 200;

    private readonly SmtpSettings _settings;
    private readonly string? _password;
    private readonly RemoteCertificateValidationCallback? _trustServer;

    /// <param name="settings">Where to connect and how to protect it.</param>
    /// <param name="password">The secret resolved at send time; null when the relay needs no authentication.</param>
    /// <param name="trustServer">
    /// Certificate validation. Null — what production passes — means the platform's own validation against the system
    /// trust store. It is a parameter because a relay behind a private CA has no other way to be trusted, and because
    /// a test has to be able to point this at a throwaway certificate without weakening anything real.
    /// </param>
    public SmtpMailTransport(
        SmtpSettings settings,
        string? password,
        RemoteCertificateValidationCallback? trustServer = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _password = password;
        _trustServer = trustServer;
    }

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var envelope = SmtpEnvelope.Create(_settings, message);

        // One deadline for the whole conversation: a relay that accepts the connection and then stalls must not hold
        // the notification job open for ever, and a timeout per operation would allow exactly that by resetting.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_settings.Timeout);

        var token = deadline.Token;

        using var client = new TcpClient();

        try
        {
            await client.ConnectAsync(_settings.Host, _settings.Port, token).ConfigureAwait(false);
        }
        catch (SocketException exception)
        {
            throw new TransientExternalFailureException(
                "notification.smtp_failed",
                $"The SMTP server {_settings.Host}:{_settings.Port} could not be reached.",
                exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TransientExternalFailureException(
                "notification.smtp_failed",
                $"The SMTP server {_settings.Host}:{_settings.Port} did not answer in time.",
                exception);
        }

        Stream stream = client.GetStream();

        try
        {
            var encrypted = _settings.Security == SmtpSecurity.ImplicitTls;

            if (encrypted)
            {
                // Port 465: the greeting only exists inside the tunnel, so the handshake comes first.
                stream = await NegotiateTlsAsync(stream, token).ConfigureAwait(false);
            }

            var session = new SmtpSession(stream);

            await session.ExpectAsync(token, "greeting", 220).ConfigureAwait(false);

            var capabilities = await session.EhloAsync(_settings.Host, token).ConfigureAwait(false);

            if (_settings.Security == SmtpSecurity.StartTls)
            {
                if (!capabilities.Contains("STARTTLS"))
                {
                    throw new PermanentExternalFailureException(
                        "notification.starttls_unsupported",
                        $"{_settings.Host}:{_settings.Port} does not offer STARTTLS. Use SSL/TLS (465), or a relay that does.");
                }

                await session.CommandAsync("STARTTLS", token, "STARTTLS", 220).ConfigureAwait(false);

                stream = await NegotiateTlsAsync(stream, token).ConfigureAwait(false);
                encrypted = true;

                // RFC 3207: everything the server said before the upgrade is void, so the session starts over.
                session = new SmtpSession(stream);
                capabilities = await session.EhloAsync(_settings.Host, token).ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(_settings.Username))
            {
                if (!encrypted)
                {
                    // Refused rather than attempted: a password on an unencrypted connection is readable by anything
                    // between here and the relay, and "the other program allowed it" is not a reason to leak it.
                    throw new PermanentExternalFailureException(
                        "notification.insecure_credentials",
                        "用户名与密码只能通过加密连接发送：请把安全方式改成 STARTTLS（587）或 SSL/TLS（465）。");
                }

                await AuthenticateAsync(session, capabilities, token).ConfigureAwait(false);
            }

            await session.CommandAsync($"MAIL FROM:<{envelope.FromAddress}>", token, "MAIL FROM", 250)
                .ConfigureAwait(false);
            await session.CommandAsync($"RCPT TO:<{envelope.ToAddress}>", token, "RCPT TO", 250, 251)
                .ConfigureAwait(false);

            await session.CommandAsync("DATA", token, "DATA", 354).ConfigureAwait(false);
            await session.WriteAsync(envelope.Content, token).ConfigureAwait(false);

            // The body is a sequence of complete lines, so the end-of-data marker is its own line.
            await session.WriteLineAsync(".", token).ConfigureAwait(false);
            await session.ExpectAsync(token, "message body", 250).ConfigureAwait(false);

            // Best effort: the message is already accepted, so a relay that dislikes QUIT is not a send failure.
            await session.TryQuitAsync(token).ConfigureAwait(false);
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task AuthenticateAsync(SmtpSession session, IReadOnlySet<string> capabilities, CancellationToken token)
    {
        var advertised = capabilities
            .Where(capability => capability.StartsWith("AUTH", StringComparison.Ordinal))
            .ToArray();

        // "AUTH PLAIN LOGIN" arrives as one token on the AUTH line, and the older "AUTH=PLAIN LOGIN" form as another.
        var offered = advertised
            .SelectMany(mechanism => mechanism.Split([' ', '='], StringSplitOptions.RemoveEmptyEntries))
            .Select(mechanism => mechanism.ToUpperInvariant())
            .ToHashSet(StringComparer.Ordinal);

        if (offered.Contains("PLAIN"))
        {
            var payload = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"\0{_settings.Username}\0{_password ?? string.Empty}"));

            await session.CommandAsync($"AUTH PLAIN {payload}", token, "AUTH", 235).ConfigureAwait(false);
            return;
        }

        if (offered.Contains("LOGIN"))
        {
            await session.CommandAsync("AUTH LOGIN", token, "AUTH", 334).ConfigureAwait(false);
            await session.CommandAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes(_settings.Username!)), token, "AUTH user", 334)
                .ConfigureAwait(false);
            await session.CommandAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes(_password ?? string.Empty)), token, "AUTH password", 235)
                .ConfigureAwait(false);
            return;
        }

        throw new PermanentExternalFailureException(
            "notification.auth_unsupported",
            $"{_settings.Host} offered no authentication this instance can use (offered: {string.Join(' ', advertised)}).");
    }

    private async Task<Stream> NegotiateTlsAsync(Stream stream, CancellationToken token)
    {
        var secure = new SslStream(stream, leaveInnerStreamOpen: false, _trustServer);

        try
        {
            await secure.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = _settings.Host,

                    // Null lets the platform choose, which on every supported runtime means TLS 1.2 and 1.3 — and keeps
                    // this from pinning a protocol version that will be wrong in five years.
                    EnabledSslProtocols = SslProtocols.None,
                },
                token).ConfigureAwait(false);
        }
        catch (AuthenticationException exception)
        {
            await secure.DisposeAsync().ConfigureAwait(false);

            // Permanent: a certificate that does not validate for this host name will not start validating either.
            throw new PermanentExternalFailureException(
                "notification.smtp_tls_failed",
                $"TLS to {_settings.Host}:{_settings.Port} failed: {exception.Message}");
        }
        catch (OperationCanceledException)
        {
            await secure.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return secure;
    }

    /// <summary>
    /// The wire form of one message: the envelope, and everything that goes after <c>DATA</c>.
    /// </summary>
    private sealed record SmtpEnvelope(string FromAddress, string ToAddress, string Content)
    {
        public static SmtpEnvelope Create(SmtpSettings settings, EmailMessage message)
        {
            var from = Address(settings.FromAddress, "发件地址");
            var to = Address(message.To, "收件地址");
            var subject = Text(message.Subject, "主题");
            var fromName = Text(settings.FromName, "发件人名称");

            var body = Convert.ToBase64String(Encoding.UTF8.GetBytes(message.Body));
            var domain = from[(from.IndexOf('@', StringComparison.Ordinal) + 1)..];

            var content = new StringBuilder();

            content.Append("From: ").Append(FormatAddress(from, fromName)).Append("\r\n");
            content.Append("To: <").Append(to).Append(">\r\n");
            content.Append("Subject: ").Append(EncodeHeaderValue(subject)).Append("\r\n");
            content.Append("Date: ").Append(DateTimeOffset.Now.ToString("ddd, dd MMM yyyy HH:mm:ss zzz", CultureInfo.InvariantCulture))
                .Append("\r\n");
            content.Append("Message-ID: <").Append(Guid.NewGuid().ToString("N")).Append('@').Append(domain).Append(">\r\n");
            content.Append("MIME-Version: 1.0\r\n");
            content.Append("Content-Type: text/plain; charset=utf-8\r\n");

            // Base64 rather than raw text: the body cannot contain a line that starts with "." (so no dot-stuffing),
            // cannot exceed the line-length limit, and cannot be mangled by a relay that rewrites bare newlines.
            content.Append("Content-Transfer-Encoding: base64\r\n\r\n");
            content.Append(Wrap(body)).Append("\r\n");

            return new SmtpEnvelope(from, to, content.ToString());
        }

        /// <summary>An address, for a command line or a header.</summary>
        private static string Address(string value, string what)
        {
            var trimmed = (value ?? string.Empty).Trim().Trim('<', '>');

            if (trimmed.Length == 0 || !trimmed.Contains('@', StringComparison.Ordinal))
            {
                throw new PermanentExternalFailureException(
                    "notification.invalid_message",
                    $"{what}「{value}」不是一个邮箱地址。");
            }

            return Text(trimmed, what);
        }

        /// <summary>
        /// A header value. CR and LF are refused rather than stripped: they are how an extra header — or an extra
        /// recipient — gets injected into a message this instance composes on someone else's behalf.
        /// </summary>
        private static string Text(string value, string what)
        {
            var text = value ?? string.Empty;

            if (text.Contains('\r', StringComparison.Ordinal) || text.Contains('\n', StringComparison.Ordinal))
            {
                throw new PermanentExternalFailureException(
                    "notification.invalid_message",
                    $"{what}里有换行符，那会往邮件头里注入内容。");
            }

            return text;
        }

        private static string FormatAddress(string address, string displayName) =>
            displayName.Length == 0 ? $"<{address}>" : $"{EncodeHeaderValue(displayName)} <{address}>";

        /// <summary>
        /// RFC 2047. A header that is pure ASCII travels as itself; anything else becomes encoded words, split on
        /// UTF-8 character boundaries so that a Chinese subject cannot be cut in half.
        /// </summary>
        private static string EncodeHeaderValue(string value)
        {
            if (value.All(character => character < 128))
            {
                return value;
            }

            var bytes = Encoding.UTF8.GetBytes(value);
            var words = new List<string>();
            var offset = 0;

            while (offset < bytes.Length)
            {
                var length = Math.Min(EncodedWordPayloadBytes, bytes.Length - offset);

                // A boundary has to fall between characters: back off while the byte *after* the chunk would be the
                // continuation of an unfinished one. (Checking the last kept byte instead is the tempting mistake —
                // it lets a chunk end on the first byte of a character and splits it in half.)
                while (length > 1 &&
                       offset + length < bytes.Length &&
                       (bytes[offset + length] & 0b1100_0000) == 0b1000_0000)
                {
                    length--;
                }

                words.Add("=?utf-8?B?" + Convert.ToBase64String(bytes, offset, length) + "?=");
                offset += length;
            }

            // Folded with CRLF + space; a decoder ignores the whitespace between two encoded words.
            return string.Join("\r\n ", words);
        }

        private static string Wrap(string base64)
        {
            var builder = new StringBuilder(base64.Length + ((base64.Length / MaximumLineLength) * 2) + 2);

            for (var offset = 0; offset < base64.Length; offset += MaximumLineLength)
            {
                if (offset > 0)
                {
                    builder.Append("\r\n");
                }

                builder.Append(base64, offset, Math.Min(MaximumLineLength, base64.Length - offset));
            }

            return builder.ToString();
        }
    }

    /// <summary>
    /// One conversation with one server: command lines out, replies in.
    /// <para>
    /// Reads a byte at a time on purpose. The replies here are a few hundred bytes in total, so the cost is
    /// irrelevant, and it means the reader can never be holding bytes from beyond the reply it was asked for — which
    /// is exactly the question that makes a buffered reader awkward across the STARTTLS upgrade.
    /// </para>
    /// </summary>
    private sealed class SmtpSession(Stream stream)
    {
        public Task WriteLineAsync(string command, CancellationToken token) => WriteAsync(command + "\r\n", token);

        public async Task WriteAsync(string text, CancellationToken token)
        {
            try
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(text), token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
            }
            catch (IOException exception)
            {
                throw new TransientExternalFailureException(
                    "notification.smtp_failed",
                    "The connection to the SMTP server broke.",
                    exception);
            }
        }

        /// <summary>Runs a command and insists on one of the expected reply codes.</summary>
        public async Task<SmtpReply> CommandAsync(string command, CancellationToken token, string what, params int[] expected)
        {
            await WriteLineAsync(command, token).ConfigureAwait(false);

            return await ExpectAsync(token, what, expected).ConfigureAwait(false);
        }

        public async Task<SmtpReply> ExpectAsync(CancellationToken token, string what, params int[] expected)
        {
            var reply = await ReadReplyAsync(token).ConfigureAwait(false);

            if (expected.Contains(reply.Code))
            {
                return reply;
            }

            // 4xx is "try again later", everything else is a refusal that retrying will not fix (§14).
            throw reply.Code is >= 400 and < 500
                ? new TransientExternalFailureException(
                    "notification.smtp_failed",
                    $"The SMTP server refused {what}: {reply.Detail}")
                : new PermanentExternalFailureException(
                    "notification.rejected",
                    $"The SMTP server rejected {what}: {reply.Detail}");
        }

        public async Task<IReadOnlySet<string>> EhloAsync(string host, CancellationToken token)
        {
            await WriteLineAsync($"EHLO {host}", token).ConfigureAwait(false);
            var reply = await ReadReplyAsync(token).ConfigureAwait(false);

            if (reply.Code == 250)
            {
                return reply.Capabilities;
            }

            // A server old enough to answer 500 to EHLO gets HELO, which offers no capabilities at all.
            await CommandAsync($"HELO {host}", token, "HELO", 250).ConfigureAwait(false);

            return new HashSet<string>(StringComparer.Ordinal);
        }

        /// <summary>QUIT, ignoring the outcome: at this point the message has already been accepted.</summary>
        public async Task TryQuitAsync(CancellationToken token)
        {
            try
            {
                await WriteLineAsync("QUIT", token).ConfigureAwait(false);
                await ReadReplyAsync(token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is TransientExternalFailureException or IOException)
            {
                // Nothing to report: the message left the building.
            }
        }

        private async Task<SmtpReply> ReadReplyAsync(CancellationToken token)
        {
            var lines = new List<string>();
            var code = 0;

            while (true)
            {
                var line = await ReadLineAsync(token).ConfigureAwait(false);

                if (line is null)
                {
                    throw new TransientExternalFailureException(
                        "notification.smtp_failed",
                        lines.Count == 0
                            ? "The SMTP server closed the connection."
                            : $"The SMTP server stopped mid-reply: {string.Join(" | ", lines)}");
                }

                lines.Add(line);

                if (line.Length >= 3 &&
                    int.TryParse(line.AsSpan(0, 3), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                {
                    code = parsed;
                }

                // "250-LINE" continues the reply, "250 LINE" ends it. Anything shorter cannot be a continuation.
                if (line.Length < 4 || line[3] != '-')
                {
                    return new SmtpReply(code, lines);
                }
            }
        }

        private async Task<string?> ReadLineAsync(CancellationToken token)
        {
            var line = new StringBuilder();
            var single = new byte[1];

            while (true)
            {
                int read;

                try
                {
                    read = await stream.ReadAsync(single, token).ConfigureAwait(false);
                }
                catch (IOException exception)
                {
                    throw new TransientExternalFailureException(
                        "notification.smtp_failed",
                        "The connection to the SMTP server broke.",
                        exception);
                }

                if (read == 0)
                {
                    return line.Length == 0 ? null : line.ToString();
                }

                if (single[0] == (byte)'\n')
                {
                    return line.ToString().TrimEnd('\r');
                }

                line.Append((char)single[0]);

                if (line.Length > 4096)
                {
                    throw new PermanentExternalFailureException(
                        "notification.rejected",
                        "The SMTP server sent a reply this client will not parse.");
                }
            }
        }
    }

    /// <summary>One reply, possibly several lines, together with the capabilities EHLO advertised.</summary>
    private sealed record SmtpReply(int Code, IReadOnlyList<string> Lines)
    {
        /// <summary>
        /// The first line, trimmed to something safe to put in a log line or a failure record. The server's own words
        /// are the only diagnostic an operator gets for a refusal, and they never contain the draft (§16).
        /// </summary>
        public string Detail =>
            Lines.Count == 0
                ? "no reply"
                : Lines[0].Length > MaximumQuotedReply ? Lines[0][..MaximumQuotedReply] + "…" : Lines[0];

        /// <summary>
        /// The words on the reply's lines: "250-SIZE 73400320" contributes SIZE, and "250 AUTH PLAIN LOGIN" keeps its
        /// arguments together as "AUTH PLAIN LOGIN", because the authentication code needs the mechanisms.
        /// </summary>
        public IReadOnlySet<string> Capabilities
        {
            get
            {
                var capabilities = new HashSet<string>(StringComparer.Ordinal);

                foreach (var line in Lines)
                {
                    if (line.Length <= 4)
                    {
                        continue;
                    }

                    var text = line[4..].Trim();
                    var separator = text.IndexOf(' ', StringComparison.Ordinal);
                    var verb = separator < 0 ? text : text[..separator];

                    if (verb.Length == 0)
                    {
                        continue;
                    }

                    capabilities.Add(verb.ToUpperInvariant());

                    if (verb.Equals("AUTH", StringComparison.OrdinalIgnoreCase) && separator > 0)
                    {
                        var mechanisms = text[(separator + 1)..].Trim();
                        capabilities.Add("AUTH " + mechanisms);
                        capabilities.Add("AUTH=" + mechanisms);
                    }
                }

                return capabilities;
            }
        }
    }
}
