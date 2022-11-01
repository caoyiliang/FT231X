using Android.App;
using Android.Content;
using Android.Hardware.Usb;

namespace FT231X
{
    public static class UsbManagerExtensions
    {
        const string ACTION_USB_PERMISSION = "com.CSoft.SerialPort.USB_PERMISSION";

        public static async Task<bool> RequestPermissionAsync(this UsbManager manager, UsbDevice device, Context context)
        {
            if (manager.HasPermission(device)) return true;
            var completionSource = new TaskCompletionSource<bool>();

            var usbPermissionReceiver = new UsbPermissionReceiver(completionSource);
            context.RegisterReceiver(usbPermissionReceiver, new IntentFilter(ACTION_USB_PERMISSION));

            var intent = PendingIntent.GetBroadcast(context, 0, new Intent(ACTION_USB_PERMISSION), PendingIntentFlags.Mutable | PendingIntentFlags.UpdateCurrent);
            manager.RequestPermission(device, intent);

            return await completionSource.Task;
        }

        class UsbPermissionReceiver : BroadcastReceiver
        {
            readonly TaskCompletionSource<bool> completionSource;

            public UsbPermissionReceiver(TaskCompletionSource<bool> completionSource)
            {
                this.completionSource = completionSource;
            }

            public override void OnReceive(Context? context, Intent? intent)
            {
                var permissionGranted = intent.GetBooleanExtra(UsbManager.ExtraPermissionGranted, false);
                context.UnregisterReceiver(this);
                completionSource.TrySetResult(permissionGranted);
            }
        }

    }
}