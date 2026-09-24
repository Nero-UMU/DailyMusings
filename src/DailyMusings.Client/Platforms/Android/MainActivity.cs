using Android.App;
using Android.Content.PM;
using Android.OS;

namespace DailyMusings.Client;

/// <summary>
/// The single Android activity.
/// <para>
/// Declared portrait on purpose. This is a one-handed capture app — the record circle and the upload button live
/// where a thumb reaches — and leaving the orientation to the sensor also means the app re-lays-out mid-recording.
/// It happens to matter for the machine this was verified on as well: the MuMu emulator reports a landscape
/// display (mRotation=ROTATION_90, 1280x720) whatever its rotation setting, so without this declaration the
/// screens would be exercised in an orientation no phone user sees.
/// </para>
/// </summary>
[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTop,
    ScreenOrientation = ScreenOrientation.Portrait,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
}
