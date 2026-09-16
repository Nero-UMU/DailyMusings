using DailyMusings.Client.Core;
using DailyMusings.Client.Core.Capture;
using DailyMusings.Client.Core.Offline;
using DailyMusings.Client.Pages;
using DailyMusings.Client.Services;
using Microsoft.Extensions.Logging;

namespace DailyMusings.Client;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();

		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			});

		// Client-side state. The offline queue lives in the app's private data directory: on Android that is the
		// app's own sandbox, which is where §9.2's "safe to disk" has to mean.
		builder.Services.AddSingleton<ClientSettings>();
		builder.Services.AddSingleton<SecureDeviceTokenProvider>();
		builder.Services.AddSingleton<PairingService>();

		builder.Services.AddSingleton<IOfflineCaptureStore>(_ =>
			new FileOfflineCaptureStore(Path.Combine(FileSystem.AppDataDirectory, "captures")));

		builder.Services.AddSingleton<IDeviceTokenProvider>(sp => sp.GetRequiredService<SecureDeviceTokenProvider>());
		builder.Services.AddSingleton<IClientClock, DeviceClock>();

		// Registered as the concrete type *and* behind the port: the capture controller wants the interface, while the
		// capture screen needs the day-listing method that only the concrete client exposes. Both must resolve to the
		// same instance, or the screen and the queue would talk to different clients.
		builder.Services.AddSingleton<DynamicCaptureApiClient>();
		builder.Services.AddSingleton<ICaptureApiClient>(sp => sp.GetRequiredService<DynamicCaptureApiClient>());
		builder.Services.AddSingleton<CaptureController>();

		// The recorder is the one platform-specific piece of the capture path.
		builder.Services.AddSingleton<IAudioRecorder, AndroidAudioRecorder>();

		builder.Services.AddTransient<TodayPage>();
		builder.Services.AddTransient<SettingsPage>();
		builder.Services.AddSingleton<AppShell>();

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}

/// <summary>The device's own clock and UTC offset, captured at the moment a thought was had.</summary>
internal sealed class DeviceClock : IClientClock
{
	public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

	public int LocalOffsetMinutes => (int)TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.UtcNow).TotalMinutes;
}
