# The release signing key

Android only installs an update over Night-loop when both are signed with the same key. Until the release key
exists, every release is signed with a throwaway key, so each one needs an uninstall first and auto-update can't
install it. Set the key up once:

1. On a PC with Java (install it with `winget install Microsoft.OpenJDK.21` if `keytool` isn't found), run:

   ```
   keytool -genkeypair -v -keystore night-loop-release.keystore -alias nightloop -keyalg RSA -keysize 4096 -validity 36500
   ```

   Pick a strong password and answer the name questions (anything is fine).
2. Turn the file into text: `[Convert]::ToBase64String([IO.File]::ReadAllBytes("night-loop-release.keystore")) | Set-Clipboard` (PowerShell).
3. In GitHub: Night-loop-Android › Settings › Secrets and variables › Actions › New repository secret, add:
   - `ANDROID_KEYSTORE_BASE64`: the text you copied
   - `ANDROID_KEYSTORE_PASSWORD`: the password
   - `ANDROID_KEY_ALIAS`: `nightloop`
4. Keep `night-loop-release.keystore` and its password somewhere safe (a password manager and a backup). If the key
   is lost, phones can't update and everyone has to uninstall and install again.

The next release after this is signed with the key. People uninstall one last time, and after that
updates install over it, including through auto-update.
