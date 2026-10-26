using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using AndroidX.Core.App;
using AndroidX.Core.Content;
using ChatAppBlazor.Services;

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
            EnableFullscreen();
            RequestRuntimePermissionsIfNeeded();
            HandleNotificationIntent(Intent);
        }

        protected override void OnResume()
        {
            base.OnResume();
            EnableFullscreen();
        }

        public override void OnWindowFocusChanged(bool hasFocus)
        {
            base.OnWindowFocusChanged(hasFocus);
            if (hasFocus)
            {
                EnableFullscreen();
            }
        }

        protected override void OnNewIntent(Android.Content.Intent? intent)
        {
            base.OnNewIntent(intent);
            HandleNotificationIntent(intent);
        }

#pragma warning disable CS0672, CA1422 // Deprecated in favour of predictive back, which this app does not enable.
        public override void OnBackPressed()
        {
            // A fullscreen video (WebView custom view) swallows the whole screen;
            // back should dismiss it rather than background the app.
            if (MainPage.AndroidMediaWebChromeClient.TryCloseFullscreenView())
            {
                return;
            }

            base.OnBackPressed();
        }
#pragma warning restore CS0672, CA1422

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

        private void EnableFullscreen()
        {
            if (Window == null)
            {
                return;
            }

            if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
            {
                Window.SetDecorFitsSystemWindows(false);
                var controller = Window.InsetsController;
                if (controller != null)
                {
                    controller.Hide(WindowInsets.Type.StatusBars() | WindowInsets.Type.NavigationBars());
                    controller.SystemBarsBehavior = 2; // BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
                }
            }
            else
            {
                var decor = Window.DecorView;
                if (decor != null)
                {
                    decor.SystemUiFlags =
                        SystemUiFlags.LayoutStable |
                        SystemUiFlags.LayoutHideNavigation |
                        SystemUiFlags.LayoutFullscreen |
                        SystemUiFlags.HideNavigation |
                        SystemUiFlags.Fullscreen |
                        SystemUiFlags.ImmersiveSticky;
                }
            }
        }

        private static void HandleNotificationIntent(Android.Content.Intent? intent)
        {
            if (intent == null)
            {
                return;
            }

            var route = intent.GetStringExtra("navigateUri");
            if (string.IsNullOrWhiteSpace(route))
            {
                return;
            }

            NotificationNavigationBridge.QueueNavigation(route);
            intent.RemoveExtra("navigateUri");
        }
    }
}
