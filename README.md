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

Scripts live in `%USERPROFILE%\DropZone\Scripts`. `Timer.ps1` and `Commands.ps1` are created
on first run.

To start one from your phone, send a LocalSend **text message** with the script name:

```
Timer 5
```

That is the whole command — no prefix needed. `run Timer 5` also works if you prefer it. The
Scripts tab shows the exact text to send for each script, so you never have to open the file
to find out.

Send **`help`** and DropZone texts back the list of everything you are allowed to start.

### What may run

Two things must both be true before anything executes:

1. **Remote start** is on in the Scripts tab (off by default)
2. that specific script has **Remote** ticked (off by default)

Anything else is an ordinary message. A word matching no enabled script does nothing, and
only the **first line** is ever considered, so a pasted document cannot smuggle a command in
further down.

### Interpreters

Each extension maps to a command line, editable under **Interpreters** in the Scripts tab and
stored in `settings.json`:

| Extension | Default |
|---|---|
| `.ps1` | `powershell -NoProfile -ExecutionPolicy Bypass -File` |
| `.py` | `py -3` |
| `.bat`, `.cmd` | `cmd /c` |
| `.js` | `node` |
| `.sh` | `bash` |

DropZone appends the quoted script path and any parameter, so `.py` runs
`py -3 "C:\...\Timer.py" 5`. Change `py -3` to `python3`, or point `.ps1` at `pwsh`, if you
prefer — the Scripts tab shows the resolved command line under **Runs**.

## Driving it from Claude

DropZone ships an MCP server, so Claude — or anything else that speaks the Model Context
Protocol — can work the phone and the network on your behalf. `DropZone.Mcp.exe` sits next
to the app in the install folder and talks stdio. Register it once:

```powershell
claude mcp add -s user dropzone B:\DropZone\DropZone.Mcp.exe
```

If Claude runs inside WSL, point it at the same file through the drive mount instead:
`/mnt/b/DropZone/DropZone.Mcp.exe`.

| Tool | What it does |
|---|---|
| `phone_status` | whether an iPhone is attached and unlocked |
| `phone_scan` | list media newest-first, stopping once it has `limit` files |
| `phone_import` | copy photos and videos into dated folders |
| `discover_peers` | announce this PC and list the devices that answer |
| `send_files` | send files to a peer, matched on alias or fingerprint |
| `transfer_history` | the transfers the tray app has recorded |

Every call is self-contained, so the tray app does not have to be running, and nothing holds
the phone or the discovery socket open afterwards. The two can run side by side — discovery
sets `SO_REUSEADDR`, so both hear announcements — but do not scan the phone from both at once.

Imports share the tray app's ledger, so a file taken by one is not taken again by the other.

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
dotnet publish src/DropZone.Mcp/DropZone.Mcp.csproj -c Release -r win-x64 `
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
src/DropZone.Mcp         MCP server exposing the above as agent tools
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
