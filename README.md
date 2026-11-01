<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="brand/logo-dark.png">
    <img src="brand/logo-light.png" alt="Untrail" width="420">
  </picture>
</p>

<h3 align="center">Uninstall completely.</h3>
<p align="center">Remove apps <em>and</em> the leftover files they leave behind on Windows.</p>

<p align="center">
  <a href="https://github.com/usmanmateen/untrail/releases/latest/download/UntrailSetup.exe"><b>⬇ Download UntrailSetup.exe</b></a>
  &nbsp;·&nbsp;
  <a href="https://github.com/usmanmateen/untrail/releases/latest">All downloads</a>
</p>

<p align="center"><img src="docs/screenshot.png" alt="Untrail showing everything a notes app left on the PC" width="820"></p>

When you uninstall an app the normal way, it often leaves gigabytes behind: caches in AppData, settings, updater folders, Start-menu shortcuts and registry entries. Untrail finds all of it and removes it in one go.

## How it works

1. **Drop an app's icon** from your desktop into the Untrail window. A Start-menu shortcut, the app's `.exe` or its install folder also work, or you can pick the app from **Installed apps…**.
2. **Review what it found.** Every item shows its size and why Untrail thinks it belongs to the app. Items marked **Safe to remove** are ticked for you. **Review** and **Keep** items stay unticked until you tick them.
3. **Click Uninstall & Clean.** The app's own uninstaller runs first, then Untrail removes whatever it left behind and tells you how much space you got back.

## Install

**[Download UntrailSetup.exe](https://github.com/usmanmateen/untrail/releases/latest/download/UntrailSetup.exe)** and run it.

- It installs for your Windows account only, so you don't need admin rights.
- It adds Untrail to the Start menu and, if you want, to the desktop.
- To remove Untrail itself, use **Settings → Apps → Installed apps → Untrail → Uninstall**.
- Prefer not to install? Download `Untrail-<version>-portable.zip` from the [releases page](https://github.com/usmanmateen/untrail/releases/latest) and run `Untrail.exe` directly.

> **"Windows protected your PC"?** Untrail isn't code-signed yet, so SmartScreen warns about it the first few times people download it. Click **More info → Run anyway**. Each release lists SHA-256 checksums in `SHA256SUMS.txt` if you want to verify your download.

Untrail needs Windows 10 or 11. It uses the .NET Framework that's already built into Windows, so there's nothing else to install.

## Where it looks

| | |
|---|---|
| **Program folder** | `Program Files`, `Program Files (x86)`, `AppData\Local\Programs` |
| **App data & caches** | `AppData\Local`, `AppData\Roaming`, `AppData\LocalLow`, `C:\Users\you\.appname`, update caches |
| **Shared data** | `ProgramData` |
| **Temporary files** | `AppData\Local\Temp` |
| **Shortcuts** | Desktop, Start menu, pinned taskbar icons |
| **Registry** | The Installed-apps entry, `Software\<App>` settings, start-up entries, App Paths |
| **Your documents** | `Documents`, `Saved Games`. These are always marked **Keep**, because they may hold your own files. |
| **Deep scan** (optional) | Folders named after the app anywhere on your drives |

## Built to be careful

- **It never touches Windows**, system folders, or top-level folders such as `Program Files`, `AppData` or `Documents`.
- **It never removes another app's files.** A folder that also holds a different installed app is skipped, even when it shares the publisher's name.
- **It refuses things it shouldn't uninstall.** Browser "web apps" (Chrome/Edge shortcuts) and built-in Windows apps get an explanation instead of a scan.
- **Files go to the Recycle Bin** by default. Tick *Delete permanently* if you want the space back straight away.
- **Registry entries are backed up** to `Documents\Untrail Backups` before they're removed (double-click a `.reg` file to restore it). A removal log is saved alongside.
- **Admin rights are asked for only when needed**, for items in `Program Files`, `ProgramData` or the machine-wide registry.

## Limitations

- Microsoft Store apps aren't supported. Remove them from Settings → Apps.
- Leftovers are matched by name. If an app keeps data in folders with unrelated names, try *Deep scan* or remove those by hand.
- Don't start Untrail with "Run as administrator". Windows blocks drag and drop into admin windows. Untrail asks for admin itself when it needs it.

## Building from source

Untrail is plain C# for .NET Framework 4.8, compiled with the `csc.exe` that ships with Windows, so no SDK or Visual Studio is needed.

```bash
powershell -ExecutionPolicy Bypass -File build.ps1 -Version 1.0.0
```

This writes `dist/Untrail.exe`, `dist/UntrailSetup.exe`, the portable zip and `SHA256SUMS.txt`.

| Path | What's there |
|---|---|
| `src/app` | The app: scanner, delete engine, window |
| `src/setup` | The installer (it embeds `Untrail.exe`) |
| `src/shared` | Brand theme and install/uninstall helpers used by both |
| `src/assets` | Logo and icon, generated from `brand/` by `tools/make_assets.py` |

To check what a scan would find without opening the window or changing anything:

```bash
Untrail.exe --scan "C:\Users\you\Desktop\Some App.lnk" --out report.txt
```

### Releasing

Push a version tag. GitHub Actions then builds on Windows and publishes the release with the installer, the portable zip and checksums:

```bash
git tag v1.0.1 && git push origin v1.0.1
```

## License

[MIT](LICENSE)
