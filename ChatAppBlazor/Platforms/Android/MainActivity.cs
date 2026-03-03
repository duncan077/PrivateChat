using Android.App;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;
using AndroidX.Core.Content;

namespace ChatAppBlazor
{
    [Activity(
        Theme = "@style/Maui.SplashTheme",
        MainLauncher = true,
        HardwareAccelerated = true,
        ConfigurationChanges = ConfigChanges.ScreenSize
            | ConfigChanges.Orientation
            | ConfigChanges.UiMode
            | ConfigChanges.ScreenLayout
            | ConfigChanges.SmallestScreenSize
            | ConfigChanges.Density)]
    public class MainActivity : MauiAppCompatActivity
    {
        private const int RuntimePermissionRequestCode = 1001;

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            RequestRuntimePermissionsIfNeeded();
        }

        public override void OnRequestPermissionsResult(
            int requestCode,
            string[] permissions,
            Permission[] grantResults)
        {
            base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        }

        private void RequestRuntimePermissionsIfNeeded()
        {
            if (Build.VERSION.SdkInt < BuildVersionCodes.M)
            {
                return;
            }

            List<string> pendingPermissions = [];

            AddPermissionIfMissing(pendingPermissions, Android.Manifest.Permission.Camera);
            AddPermissionIfMissing(pendingPermissions, Android.Manifest.Permission.RecordAudio);

            if (Build.VERSION.SdkInt <= (BuildVersionCodes)32)
            {
                AddPermissionIfMissing(
                    pendingPermissions,
                    Android.Manifest.Permission.ReadExternalStorage);
            }

            if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
            {
                AddPermissionIfMissing(
                    pendingPermissions,
                    "android.permission.READ_MEDIA_IMAGES");
                AddPermissionIfMissing(
                    pendingPermissions,
                    "android.permission.READ_MEDIA_VIDEO");
                AddPermissionIfMissing(
                    pendingPermissions,
                    "android.permission.READ_MEDIA_AUDIO");
                AddPermissionIfMissing(
                    pendingPermissions,
                    "android.permission.POST_NOTIFICATIONS");
            }

            if (Build.VERSION.SdkInt >= BuildVersionCodes.UpsideDownCake)
            {
                AddPermissionIfMissing(
                    pendingPermissions,
                    "android.permission.READ_MEDIA_VISUAL_USER_SELECTED");
            }

            if (pendingPermissions.Count > 0)
            {
                ActivityCompat.RequestPermissions(
                    this,
                    pendingPermissions.ToArray(),
                    RuntimePermissionRequestCode);
            }
        }

        private void AddPermissionIfMissing(List<string> pendingPermissions, string permission)
        {
            if (ContextCompat.CheckSelfPermission(this, permission) != Permission.Granted)
            {
                pendingPermissions.Add(permission);
            }
        }
    }
}
