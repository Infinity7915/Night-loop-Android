# Night-loop for Android

The Android version of [Night-loop](https://github.com/Infinity7915/Night-loop): build macros from blocks and run
them from a floating Night-loop bubble that stays on top of other apps and the home screen.

**Status: phase 1 preview.** The app installs, walks you through the Android permissions it needs, shows the
floating bubble and checks the Night-loop server. Signing in and playing macros come in phase 2.

## How it works

| Part | Android feature | What it's for |
| --- | --- | --- |
| Floating bubble | Display over other apps (`SYSTEM_ALERT_WINDOW`) + a foreground service | Pick and play macros over any app |
| Night-loop accessibility service | `AccessibilityService` with `canPerformGestures` | Taps, swipes, Back/Home while a macro you started plays |
| Screen reading (phase 3) | Screen capture + on-device OCR | Find & click, Scan & turn, Wait for text/image/pixel |

Macros are made from blocks only (no recording), live on the same Night-loop account as the PC app, and are marked
as Android macros: the phone plays Android macros and the PC plays PC macros.

## Layout

```
external/night-loop        git submodule: the PC app repo, for the shared projects
  src/NightLoop.Contracts    macro format, validator, limits, API models (also used by the server)
  src/NightLoop.Client       API client (sign-in, sync, autosave)
  src/NightLoop.Engine       macro engine core (no Windows code)
src/NightLoop.Android      the Android app (.NET 10 for Android, C#)
```

Clone with `git clone --recursive`, or run `git submodule update --init` after cloning.

## Building

Needs the .NET 10 SDK, `dotnet workload install android`, a JDK (17 or 21) and the Android SDK.

```
dotnet build src/NightLoop.Android -p:AndroidSdkDirectory=/path/to/android-sdk
```

CI (`.github/workflows/build.yml`) builds a Release `.apk` on every push and pull request and attaches it to the
run as `night-loop-android-apk`.

## Installing a preview build

1. Download `night-loop-android-apk` from the latest successful **Build** run and unzip it.
2. Open the `.apk` on your phone and allow installing from that source when Android asks.
3. Open Night-loop and turn on each item in **Set up**. On Android 13 and newer, if the accessibility switch
   says it's restricted: Settings › Apps › Night-loop › ⋮ › **Allow restricted settings**, then try again.
