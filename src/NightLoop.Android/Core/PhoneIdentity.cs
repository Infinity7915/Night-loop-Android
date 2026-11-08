using System.Security.Cryptography;
using System.Text;
using Android.Content;
using Android.OS;
using NightLoop.Client;
using NightLoop.Contracts.Api;

namespace NightLoop.Mobile.Core;

internal static class PhoneIdentity
{
    /// <summary>
    /// Like the PC's: the install id plus a SHA-256 of the phone's Android id (unique per app and phone, the raw
    /// value never leaves the phone). Label is the phone model, e.g. "Android · Pixel 8".
    /// </summary>
    public static DeviceIdentity Create(Context context)
    {
        var androidId = global::Android.Provider.Settings.Secure.GetString(context.ContentResolver, global::Android.Provider.Settings.Secure.AndroidId) ?? "";
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"android|{androidId}|{Build.Manufacturer}|{Build.Model}"))).ToLowerInvariant();
        var model = (Build.Model ?? "phone").Trim();
        var label = $"Android · {model}";
        if (label.Length > AccountRules.DeviceLabelMaxLength) label = label[..AccountRules.DeviceLabelMaxLength];
        return new DeviceIdentity(AppInfo.InstallId(context), fingerprint, label);
    }
}
