# Barracuda Battery

See your **Razer Barracuda X (2022)** battery level in the Windows tray while it's connected through the **2.4 GHz USB dongle**. Razer Synapse doesn't support this headset, and Windows only shows its battery over Bluetooth.

- The tray icon shows the battery level in 5% steps, colored green, yellow or red - and blue while charging
- The level is estimated from the headset's voltage (it reports nothing else over 2.4 GHz); while the charger is plugged in the voltage is useless, so the app detects charging and keeps showing the last level instead of a wrong one
- Hover the icon for the exact voltage, e.g. `Barracuda X: 70% (3.98 V)`
- Updates every 30 seconds, and picks the headset back up within ~10 seconds after it was off or on Bluetooth; double-click the icon to refresh right away
- Low-battery notification at 20%
- Optional "Start with Windows"
- Tiny (~20 KB), no installer, no Razer software and no extra runtime needed

## Download and run

1. Download `BarracudaBattery.exe` from the [Releases](../../releases) page.
2. Plug in the dongle, turn on the headset, and double-click `BarracudaBattery.exe`.
3. Windows 11 hides new tray icons under the **^** arrow next to the clock. Drag the icon onto the taskbar to keep it visible.

Right-click the icon for Refresh, Start with Windows, and Exit.

## Supported hardware

| Dongle USB ID | Bridge chip | Status |
|---|---|---|
| `1532:0550` | YS-Tech | Tested |
| `1532:0552` | Macronix | Implemented, untested |

Check your dongle's ID in Device Manager, under the dongle's Properties > Details > Hardware IDs.

## Troubleshooting

- **The icon shows `--`:** the headset is off, connected over Bluetooth, or out of range. The battery level comes back within about 10 seconds after the headset reconnects to the dongle.
- **Something else is wrong:** the app keeps a small log of connection changes in `%LOCALAPPDATA%\BarracudaBattery\log.txt` (paste that path into the File Explorer address bar). Attach it when you [open an issue](../../issues).

## How it works

The headset reports its battery voltage, not a percentage. The app sends the headset's "get battery" command through the dongle, then converts the voltage with a Li-ion discharge curve calibrated against Razer's mobile app. The protocol details are documented in [`src/Protocol.cs`](src/Protocol.cs).

The app only sends read-only status commands. It never writes settings or firmware.

## Build from source

No SDK needed. Windows includes the C# compiler this project uses.

```
build.cmd
```

This produces `bin\BarracudaBattery.exe` (the tray app) and `bin\probe.exe` (a command-line tool that prints the raw exchange with the dongle, useful for debugging). If the tray app is running, the build closes it and starts the new version afterwards.

## Credits

- Made by [Mehdi Bouchami](https://github.com/mehdibouchami)
- The `0552` (Macronix) dongle protocol is based on [razer-barracuda-battery-tray](https://github.com/KhromotozzDevOut/razer-barracuda-battery-tray) (MIT)

## License

[MIT](LICENSE)

Not affiliated with or endorsed by Razer Inc. "Razer" and "Barracuda" are trademarks of Razer Inc.
