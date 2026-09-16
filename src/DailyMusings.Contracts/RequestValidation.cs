namespace DailyMusings.Contracts;

/// <summary>
/// Validation rules shared by the server and its clients (docs/开发指导.md §5: "API DTO 与共享验证规则").
/// <para>
/// Only shape and length limits live here. Everything that is a <em>rule</em> about the product — what makes
/// a draft stale, when a publication may run — belongs to the domain, so there is exactly one place where it
/// can be wrong.
/// </para>
/// </summary>
public static class FieldLimits
{
    public const int DeviceNameMaxLength = 64;
    public const int PlatformMaxLength = 32;
    public const int PairingCodeMaxLength = 32;
}

public static class RequestValidation
{
    public static bool TryValidateDeviceRegistration(
        RedeemPairingCodeRequest? request,
        out string? failure)
    {
        if (request is null)
        {
            failure = "A request body is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.Code))
        {
            failure = "A pairing code is required.";
            return false;
        }

        if (request.Code.Length > FieldLimits.PairingCodeMaxLength)
        {
            failure = "The pairing code is too long to be a valid code.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.DeviceName))
        {
            failure = "A device name is required.";
            return false;
        }

        if (request.DeviceName.Length > FieldLimits.DeviceNameMaxLength)
        {
            failure = $"A device name may not exceed {FieldLimits.DeviceNameMaxLength} characters.";
            return false;
        }

        if (request.Platform is { Length: > FieldLimits.PlatformMaxLength })
        {
            failure = $"A platform may not exceed {FieldLimits.PlatformMaxLength} characters.";
            return false;
        }

        failure = null;
        return true;
    }
}
