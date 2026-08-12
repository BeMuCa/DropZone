# DropZone

A small Windows tray tool that moves photos, video and files between an iPhone and a PC —
and between PCs — without iTunes and without an Apple account.

Two independent paths:

| Path | Direction | How |
|---|---|---|
| **Cable** | iPhone → PC only | Windows' inbox MTP/WPD driver. No iTunes, no Apple Mobile Device Support. |
| **Wireless** | both ways | LocalSend v2 protocol, so it interoperates with the stock LocalSend apps. |

DropZone is **not** a fork of LocalSend. It is an independent implementation of the
[published protocol](https://github.com/localsend/protocol).

## Install

Grab `DropZone-win-x64` from the latest [Actions run](../../actions) or
[release](../../releases), unzip it, then:

```powershell
.\install.ps1                              # installs to B:\DropZone
.\install.ps1 -InstallDir 'D:\Tools\DropZone'
.\install.ps1 -Autostart                   # also start with Windows
```

No administrator rights required. It creates a Start Menu shortcut and, with `-Autostart`,
one `HKCU\...\Run` entry.

**Prerequisite:** the .NET 10 Desktop Runtime.

```powershell
winget install Microsoft.DotNet.DesktopRuntime.10
```

## Uninstall

```powershell
.\uninstall.ps1                    # keeps your files and settings
.\uninstall.ps1 -RemoveData        # also deletes them
```

It stops the app, removes the install folder, the Start Menu shortcut and the autostart
entry. Everything DropZone writes lives in exactly two places:

- `%USERPROFILE%\DropZone` — received files, imports, scripts, history
- `%APPDATA%\DropZone` — settings

## Using it

The tray icon opens a panel in the corner with four tabs.

- **Send** — pick a device, add files (button or drag-and-drop), Send. The text box below
  sends a plain message; `run <script>` starts a script on the other device.
- **Receive** — the on/off switch and a history grouped per transfer, tagged 📱 or 🖥 by
  sender and clickable to open the files.
- **iPhone** — Scan, then choose what to import. Split into photos and videos, newest first.
- **Scripts** — everything in `Scripts/`, each with a parameter box, Run, Edit and a
  per-script Remote toggle.

📌 pins the panel open when it loses focus. Turn it on before dragging files in — otherwise
the drag steals focus and the panel hides mid-drag.

Quit from the tray icon's right-click menu.

## Scripts

Scripts live in `%USERPROFILE%\DropZone\Scripts` (`.ps1`, `.bat`, `.cmd`). A `Timer.ps1`
example is created on first run.

To start one from your phone, send a LocalSend **text message**:

```
run Timer 5
```

Three things must all be true before anything executes:

1. **Remote start** is on in the Scripts tab (off by default)
2. that specific script has **Remote** ticked (off by default)
3. the message *begins* with `run`

A message that merely mentions the word — `running late, sorry`, or a document containing
`run backup` on a later line — is never treated as a command.

## Why the cable is one-way

iOS exposes its camera roll over MTP read-only. Writing is refused at the driver level:

```
NewFolder -> Zugriff verweigert (HRESULT: 0x80030005 STG_E_ACCESSDENIED)
CopyHere  -> silently rejected, item count unchanged
```

So PC → iPhone transfers go over the wireless path instead. This is a limitation of iOS,
not of the tool.

**The phone must be unlocked** whenever you scan or import. iOS hides the camera roll while
the screen is locked, even after you have tapped Trust, so DropZone will say so rather than
claim no phone is attached.

## Layout on the phone

iOS does **not** present a `DCIM` folder over MTP. Internal Storage contains date-coded
folders directly — `202508_b`, `202607__`, `YYYYMM` plus a two-character suffix. DropZone
parses these and files imports into `iPhone/2025/2025-08/`. Anything unparseable lands in
`Unsorted/`.

## Build from source

The projects target `net10.0-windows`, so build on Windows:

```powershell
dotnet build DropZone.slnx
dotnet test  DropZone.slnx
dotnet publish src/DropZone.App/DropZone.App.csproj -c Release -r win-x64 `
  --self-contained false -p:PublishSingleFile=true -o publish
```

Note the solution is `DropZone.slnx`, not `.sln` — .NET 10 emits the XML solution format.

If you keep the source in WSL and build with the Windows SDK over a `\\wsl.localhost\...`
UNC path, set an explicit local `ContentRootPath` on any ASP.NET host: the default
`WebApplication.CreateBuilder()` hangs forever when the content root is a UNC path.

```
src/DropZone.Mtp         cable import: folder parsing, planning, ledger, MediaDevices source
src/DropZone.LocalSend   protocol v2: discovery, Kestrel receiver, sender
src/DropZone.App         WPF tray app (H.NotifyIcon)
src/DropZone.Cli         diagnostics for the cable path
tests/                   xunit
```

### Diagnostics

```powershell
DropZone.Cli status            # is a phone connected and unlocked?
DropZone.Cli scan              # list media the phone exposes
DropZone.Cli import <folder>   # run a full import
```

## Networking notes

Discovery joins the multicast group and announces on **every** up, multicast-capable
interface rather than letting Windows choose. Windows picks by route metric, and a
Hyper-V/WSL virtual adapter advertises 10 Gbps against real WiFi's ~780 Mbps — so the
default choice announces into the virtual network where no phone can hear it.

The status line at the bottom of the panel shows which addresses are actually joined.

## Security notes

- Incoming filenames are untrusted: path components are stripped and the resolved path is
  asserted to stay inside the download folder.
- Upload tokens are compared in fixed time and pinned to the originating IP.
- One transfer session at a time; a second concurrent request gets `409`.
- Peers use self-signed certificates by design — the protocol pins trust to the
  certificate's SHA-256 ("fingerprint") rather than a CA chain.
- Remote script execution is off by default at two independent levels.

## Known gaps

- The cable path's `MediaDevicesPhoneSource` has **not** been verified against a real
  unlocked iPhone yet. Everything above it is covered by tests using a fake source.
- Interoperability with the official LocalSend apps is **untested** — the transfer tests
  run DropZone against DropZone.
- No PIN support on incoming transfers, and no per-transfer accept prompt in the UI
  (`ApproveTransfer` exists as a hook, defaulting to accept).

## Licence

MIT — see [LICENSE](LICENSE).
