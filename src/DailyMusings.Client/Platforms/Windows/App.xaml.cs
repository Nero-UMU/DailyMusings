namespace DailyMusings.Client.WinUI;

/// <summary>
/// The Windows entry point (docs/开发指导.md §18).
/// <para>
/// Everything it could have decided lives in <see cref="MauiProgram"/>, which the Android client also calls: the
/// pages, the offline queue, the API client and the capture controller are the same objects on both platforms, and
/// only the recorder is chosen per platform.
/// </para>
/// </summary>
public partial class App : MauiWinUIApplication
{
    public App() => InitializeComponent();

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
