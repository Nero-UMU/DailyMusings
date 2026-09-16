using DailyMusings.Application.Admin;

namespace DailyMusings.Server.Startup;

/// <summary>
/// Creates the administrator account on first start and surfaces the generated password
/// (docs/开发指导.md §10.1).
/// <para>
/// The password is written to <see cref="Console"/> directly, never through <c>ILogger</c>. That is the whole
/// point of the rule: anything logged can end up in a log file, a log shipper or a support bundle, while
/// stdout during startup is seen once by the person who is standing up the instance.
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
        var result = await initialize.ExecuteAsync(cancellationToken).ConfigureAwait(false);

        if (!result.Created)
        {
            // Naming the account is safe; naming anything about its credentials would not be.
            logger.LogInformation("Administrator account {Username} is already initialized.", result.Username);
            return;
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
}
