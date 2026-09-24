using DailyMusings.Client.Core;
using DailyMusings.Client.Core.Capture;
using DailyMusings.Client.Core.Offline;
using DailyMusings.Client.Core.Audio;
using DailyMusings.Client.Core.Reflections;
using DailyMusings.Client.Core.Settings;
using DailyMusings.Client.Core.Topics;
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

		// The draft screen talks to the reflection endpoints, which are a separate contract with their own failure
		// classification (several of their refusals are instructions to the user rather than errors).
		builder.Services.AddSingleton<DynamicReflectionApiClient>();
		builder.Services.AddSingleton<IReflectionApiClient>(sp => sp.GetRequiredService<DynamicReflectionApiClient>());

		// The two reads that belong to the instance rather than to the capture queue: an entry's stored recording
		// (§15.2 step 6) and the notification preferences (§9.3, §12).
		builder.Services.AddSingleton<DynamicInstanceApiClient>();
		builder.Services.AddSingleton<INotificationSettingsApiClient>(sp => sp.GetRequiredService<DynamicInstanceApiClient>());
		builder.Services.AddSingleton<IModelNameApiClient>(sp => sp.GetRequiredService<DynamicInstanceApiClient>());
		builder.Services.AddSingleton(new AudioClipCache(Path.Combine(FileSystem.CacheDirectory, "playback")));

		// The topic vocabulary: browsing, renaming, merging and filing by hand (§6.2, §9.3 主题页).
		builder.Services.AddSingleton<DynamicTopicApiClient>();
		builder.Services.AddSingleton<ITopicApiClient>(sp => sp.GetRequiredService<DynamicTopicApiClient>());

		// The recorder is the one platform-specific piece of the capture path. Everything else — the offline queue,
		// the upload, the pages — is the same code, which is what §5's Client.Core split was for.
#if ANDROID
		builder.Services.AddSingleton<IAudioRecorder, AndroidAudioRecorder>();
		builder.Services.AddSingleton<IAudioPlayer, AndroidAudioPlayer>();
#else
#error Every target needs an IAudioRecorder; add one for this platform rather than shipping a client that cannot record.
#endif

		builder.Services.AddTransient<TodayPage>();
		builder.Services.AddTransient<DraftPage>();
		builder.Services.AddTransient<TopicsPage>();
		builder.Services.AddTransient<CalendarPage>();
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
