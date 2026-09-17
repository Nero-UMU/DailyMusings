namespace DailyMusings.Client.Core;

/// <summary>
/// One answer from the server, with the failure already classified — the shape every read and write in the client
/// returns (docs/开发指导.md §9.2, §13).
/// <para>
/// Three outcomes have to stay distinguishable, because they mean different things to a person: the request was
/// answered, the request was refused (a code the user can act on), or nothing came back at all. Collapsing them is
/// how an unreachable server came to look like an empty day, and how a day with no draft came to look like a broken
/// network — both found on a real device rather than in a test.
/// </para>
/// </summary>
public sealed record ApiResult<T>(T? Value, bool ServerReached, string? FailureCode)
{
    public bool Succeeded => ServerReached && FailureCode is null && Value is not null;

    public static ApiResult<T> From(T value) => new(value, true, null);

    /// <summary>The request never answered, so nothing is known.</summary>
    public static ApiResult<T> Unreachable(string failureCode) => new(default, false, failureCode);

    /// <summary>The server answered and refused, or answered with something unusable.</summary>
    public static ApiResult<T> Refused(string failureCode) => new(default, true, failureCode);
}
