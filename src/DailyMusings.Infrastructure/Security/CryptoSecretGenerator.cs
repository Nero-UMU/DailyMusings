using System.Security.Cryptography;
using System.Text;
using DailyMusings.Application.Abstractions;

namespace DailyMusings.Infrastructure.Security;

/// <summary>
/// Generates device tokens, pairing codes and the bootstrap password (docs/开发指导.md §10.1, §10.2).
/// <para>
/// Human-facing secrets (pairing codes, initial password) use an alphabet with <c>I</c>/<c>O</c>/<c>0</c>/
/// <c>1</c> removed, because these values get read aloud, retyped and mistyped. Machine-facing tokens are
/// plain base64url over 32 random bytes.
/// </para>
/// </summary>
public sealed class CryptoSecretGenerator : ISecretGenerator
{
    /// <summary>Unambiguous alphabet: no I, O, 0 or 1.</summary>
    public const string HumanAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    private const int PairingCodeLength = 8;
    private const int PairingCodeGroupSize = 4;
    private const int InitialPasswordLength = 24;
    private const int TokenBytes = 32;

    public GeneratedSecret GenerateDeviceToken()
    {
        var token = Base64Url(RandomNumberGenerator.GetBytes(TokenBytes));
        return new GeneratedSecret(token, HashToken(token));
    }

    public GeneratedSecret GeneratePairingCode()
    {
        var characters = new char[PairingCodeLength];
        for (var i = 0; i < characters.Length; i++)
        {
            // GetInt32 is rejection-sampled internally, so the alphabet stays uniform.
            characters[i] = HumanAlphabet[RandomNumberGenerator.GetInt32(HumanAlphabet.Length)];
        }

        var compact = new string(characters);
        var grouped = string.Concat(
            compact.AsSpan(0, PairingCodeGroupSize),
            "-",
            compact.AsSpan(PairingCodeGroupSize));

        return new GeneratedSecret(grouped, HashPairingCode(grouped));
    }

    public string GenerateInitialPassword()
    {
        var characters = new char[InitialPasswordLength];
        for (var i = 0; i < characters.Length; i++)
        {
            characters[i] = HumanAlphabet[RandomNumberGenerator.GetInt32(HumanAlphabet.Length)];
        }

        return new string(characters);
    }

    public string HashToken(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return Sha256Hex(token);
    }

    public string HashPairingCode(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        return Sha256Hex(NormalizePairingCode(code));
    }

    /// <summary>Uppercases and strips the visual separator, so retyped codes still match.</summary>
    public static string NormalizePairingCode(string code)
    {
        var builder = new StringBuilder(code.Length);

        foreach (var character in code)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToUpperInvariant(character));
            }
        }

        return builder.ToString();
    }

    private static string Sha256Hex(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
