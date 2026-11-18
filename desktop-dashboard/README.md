# Android Desk

A Windows companion for scrcpy: keep Android screens as movable desktop windows,
manage them from a device list, or tuck the controls into a compact floating bar
or the system tray. This fork adds the companion in `desktop-dashboard/`; upstream
scrcpy remains intact under its existing Apache 2.0 license.

## Features

- Open, focus and close individual screens; open all and tile across the dashboard's monitor.
- Save screen positions when closing through the dashboard, tiling, or exiting.
- Rename devices and retain connection details locally.
- Disconnect individual Wi-Fi devices without stopping the others.
- Close the dashboard to hide to tray; use **Exit Android Desk** to close the app and its screens.
- **Add device** wizard: automatically detect USB devices and authorization status,
  configure USB-assisted Wi-Fi, or pair Android 11+ using wireless debugging and mDNS discovery.
- Enter a pairing address manually when a network blocks discovery. Pairing and connection
  use separate ports. Pairing codes are never saved in dashboard settings.

Android's on-device debugging switches and authorization prompts require the user's
interaction. USB-assisted Wi-Fi generally needs re-enabling after a reboot. Android's
saved wireless pairing can reconnect without USB, but wireless debugging may need
switching back on and the port/address can change. This app does not promise permanent
unattended access, unlock phones, or remove Android permission requirements.

## Build and install

Requires Windows and .NET 8 SDK to build; .NET 8 Desktop Runtime to run.

```powershell
dotnet build desktop-dashboard/AndroidDesk.csproj -c Release
dotnet run --project desktop-dashboard/tests/LogicTests.csproj -c Release
powershell -NoProfile -File desktop-dashboard/Install.ps1
```

The installer uses the official scrcpy 5.0.1 Windows x64 archive, verifies its SHA-256,
and creates an **Android Desk** desktop shortcut. Existing scrcpy installations at
`%LOCALAPPDATA%/Programs/scrcpy/scrcpy-win64-v5.0.1` are reused.
Set `ANDROID_DESK_SCRCPY_DIR` to use a different directory containing scrcpy.exe and adb.exe.

Settings and device addresses stay in `%LOCALAPPDATA%/AndroidDesk/settings.json`.
The previous Android Screens launcher's saved connections are imported on first run.
No device identifiers, addresses, keys or settings belong in this repository.

Screen controls operate only on scrcpy processes started by this dashboard. Existing
independent scrcpy windows remain independent; close them before using Open all to avoid duplicates.
USB devices are disconnected by unplugging the cable; **Close screen** only stops mirroring.

This first version is a desktop controller with separate screen windows, rather than
embedded video tiles. Compact mode stays on top; the Windows notification-area icon
always provides Show controls, Tile screens, per-device screen and Wi-Fi controls,
Close all screens, and Exit.
