using DailyMusings.Application.Admin;
using DailyMusings.Domain.Identity;
using Microsoft.Extensions.Configuration;

namespace DailyMusings.Server.Startup;

/// <summary>
/// Creates the administrator account on first start and surfaces the generated password
/// (docs/开发指导.md §10.1).
/// <para>
/// The password is written to <see cref="Console"/> directly, never through <c>ILogger</c>. That is the whole
/// point of the rule: anything logged can end up in a log file, a log shipper or a support bundle, while
/// stdout during startup is seen once by the person who is standing up the instance.
/// </para>
/// <para>
/// When the deployment supplies the password (<c>Admin:InitialPassword</c> — the shipped Compose file's
/// <c>DM_ADMIN_PASSWORD</c>) there is nothing to reveal, because the operator already has it. It is deliberately
/// <em>not</em> echoed: container stdout is kept by Docker for as long as the container lives, and a configured
/// password sitting in <c>docker compose logs</c> from then on is exactly what this rule exists to prevent.
/// </para>
/// </summary>
public static class AdminBootstrap
{
    public static async Task RunAsync(
        IServiceProvider services,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(logger);

        await using var scope = services.CreateAsyncScope();
        var initialize = scope.ServiceProvider.GetRequiredService<InitializeAdminUseCase>();

        // Read from deployment configuration: at this moment there is no administrator to have saved a setting,
        // and the account the password belongs to does not exist yet.
        var preferred = scope.ServiceProvider
            .GetRequiredService<IConfiguration>()["Admin:InitialPassword"];

        var result = await initialize.ExecuteAsync(cancellationToken, preferred).ConfigureAwait(false);

        if (!result.Created)
        {
            // Naming the account is safe; naming anything about its credentials would not be.
            logger.LogInformation("Administrator account {Username} is already initialized.", result.Username);
            return;
        }

        if (result.PasswordFromDeployment)
        {
            WriteDeploymentPasswordBanner(result.Username);

            logger.LogInformation(
                "Created the initial administrator account with the password from deployment configuration. That " +
                "password is intentionally absent from this log.");
            return;
        }

        if (!string.IsNullOrWhiteSpace(preferred))
        {
            // The operator asked for a specific password and did not get it. Saying so is the difference between
            // "this product ignored my setting" and "my setting did not meet the rule".
            WriteUnusableConfiguredPasswordNotice();
        }

        WriteBanner(result.Username, result.GeneratedPassword!);

        logger.LogInformation(
            "Created the initial administrator account. The generated password was printed to the terminal and " +
            "is intentionally absent from this log.");
    }

    private static void WriteBanner(string username, string password)
    {
        var writer = Console.Out;

        writer.WriteLine();
        writer.WriteLine("================================================================");
        writer.WriteLine(" 每日随想：已创建管理员账号");
        writer.WriteLine();
        writer.WriteLine($"   账号：{username}");
        writer.WriteLine($"   密码：{password}");
        writer.WriteLine();
        // Machine-readable duplicate of the password, for the operator who needs to script
        // "docker compose logs | grep INITIAL-ADMIN-PASSWORD". Same channel, same one-time exposure.
        writer.WriteLine($"INITIAL-ADMIN-PASSWORD={password}");
        writer.WriteLine();
        writer.WriteLine(" 首次登录会强制要求修改账号名与密码。");
        writer.WriteLine(" 这串密码只在此处显示一次，不会写入日志文件，也无法再次查看。");
        writer.WriteLine("================================================================");
        writer.WriteLine();
        writer.Flush();
    }

    /// <summary>
    /// The configured-password counterpart of <see cref="WriteBanner"/>: same channel, same one-time visibility,
    /// but the value itself stays out of it.
    /// </summary>
    private static void WriteDeploymentPasswordBanner(string username)
    {
        var writer = Console.Out;

        writer.WriteLine();
        writer.WriteLine("================================================================");
        writer.WriteLine(" 每日随想：已创建管理员账号");
        writer.WriteLine();
        writer.WriteLine($"   账号：{username}");
        writer.WriteLine("   密码：部署配置里的那个（DM_ADMIN_PASSWORD）");
        writer.WriteLine();
        writer.WriteLine(" 首次登录会强制要求修改账号名与密码。");
        writer.WriteLine(" 容器日志会被长期保存，所以这里刻意不回显这串密码。");
        writer.WriteLine("================================================================");
        writer.WriteLine();
        writer.Flush();
    }

    /// <summary>
    /// Reports a configured password that did not clear the domain rule. Not an error: the instance comes up with a
    /// generated password, which the banner right below prints, so nobody is locked out of a fresh instance.
    /// </summary>
    private static void WriteUnusableConfiguredPasswordNotice()
    {
        var writer = Console.Out;

        writer.WriteLine();
        writer.WriteLine("================================================================");
        writer.WriteLine(" 注意：部署配置里的初始管理员密码没有采用");
        writer.WriteLine();
        writer.WriteLine("   DM_ADMIN_PASSWORD 需要满足密码规则：");
        writer.WriteLine(
            $"   {AdminAccount.MinPasswordLength}–{AdminAccount.MaxPasswordLength} 位，且不能是空白。");
        writer.WriteLine("   已改为随机生成，就是下面这一串。");
        writer.WriteLine("================================================================");
        writer.WriteLine();
        writer.Flush();
    }
}
