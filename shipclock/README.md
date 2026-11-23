# Ship Clock

Android app that reminds you to change the phone's time zone at scheduled times
(whole-hour ship's clock changes at sea). It cannot change the zone itself, because a
normal app is not allowed to. It posts a notification, and you change the zone by hand.

**Download:** https://github.com/mestraluun/OpenVSP-Hydro/releases/download/shipclock-latest/ShipClock.apk

## Use
1. Pick the date and time of the change **in ship's time**. For queued changes, the time
   is read in the zone set by the previous scheduled change.
2. Pick the new offset (UTC-12 … UTC+14), or type an IANA zone such as `Asia/Dubai`.
3. At that time a notification appears: *Change clocks to UTC+4*. Its text is
   *Time zone > UTC offset > GMT+04:00*, the path on Samsung.
   Tap it to open Date & time settings.
4. Once the phone's zone matches, the reminder clears itself. You can also tap **Done**.

After a reboot the alarms are rescheduled. Only the latest missed change fires; older
missed changes are marked skipped.

## Build
GitHub Actions (`.github/workflows/shipclock.yml`) builds a debug APK with AGP 8.7.3,
Gradle 8.9 and Java 17. It publishes the APK to the `shipclock-latest` release. The
committed `app/debug.keystore` (password `android`) keeps the signature stable, so every
build installs over the last one. It is for personal use only.
