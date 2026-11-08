using System.Text.Json;
using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using NightLoop.Client;

namespace NightLoop.Mobile.Core;

/// <summary>
/// The phone's version of the PC's session.bin: the session and trust tokens, encrypted with an AES key that lives
/// in the Android Keystore and never leaves it.
/// </summary>
internal sealed class KeystoreSessionStore(string path) : ISessionStore
{
    private const string Alias = "nightloop_session";
    private const int IvLength = 12;

    public StoredSession Load()
    {
        try
        {
            if (!File.Exists(path)) return new StoredSession();
            var data = File.ReadAllBytes(path);
            if (data.Length <= IvLength) return new StoredSession();
            var cipher = Cipher.GetInstance("AES/GCM/NoPadding")!;
            cipher.Init(CipherMode.DecryptMode, Key(), new GCMParameterSpec(128, data[..IvLength]));
            var plain = cipher.DoFinal(data[IvLength..])!;
            return JsonSerializer.Deserialize<StoredSession>(plain) ?? new StoredSession();
        }
        catch (Exception ex) when (ex is Java.Lang.Exception or IOException or JsonException)
        {
            // Unreadable (key reset after a restore, corrupted file): start signed out.
            return new StoredSession();
        }
    }

    public void Save(StoredSession session)
    {
        var cipher = Cipher.GetInstance("AES/GCM/NoPadding")!;
        cipher.Init(CipherMode.EncryptMode, Key());
        var iv = cipher.GetIV()!;
        var sealedBytes = cipher.DoFinal(JsonSerializer.SerializeToUtf8Bytes(session))!;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [.. iv, .. sealedBytes]);
    }

    private static IKey Key()
    {
        var store = KeyStore.GetInstance("AndroidKeyStore")!;
        store.Load(null as KeyStore.ILoadStoreParameter);
        if (store.GetKey(Alias, null) is { } existing) return existing;
        var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, "AndroidKeyStore")!;
        generator.Init(new KeyGenParameterSpec.Builder(Alias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetBlockModes(KeyProperties.BlockModeGcm)
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)
            .Build());
        return generator.GenerateKey()!;
    }
}
