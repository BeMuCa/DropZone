# Dropzone

A small Windows tray tool that moves photos, video and files between an iPhone and a PC —
and between PCs — without iTunes and without an Apple account.

Two independent paths:

| Path | Direction | How |
|---|---|---|
| **Cable** | iPhone → PC only | Windows' inbox MTP/WPD driver. No iTunes, no Apple Mobile Device Support. |
| **Wireless** | both ways | LocalSend v2 protocol, so it interoperates with the stock LocalSend apps. |

## Why the cable is one-way

iOS exposes its camera roll over MTP read-only. Writing is refused at the driver level:

```
NewFolder -> Zugriff verweigert (HRESULT: 0x80030005 STG_E_ACCESSDENIED)
CopyHere  -> silently rejected, item count unchanged
```

So PC → iPhone transfers go over the wireless path instead. This is a limitation of iOS, not of the tool.

## Layout on the phone

iOS does **not** present a `DCIM` folder over MTP. Internal Storage contains date-coded folders
directly — `202508_b`, `202607__`, `YYYYMM` plus a two-character suffix. Dropzone parses these and
files imports into `<PhotoFolder>/2025/2025-08/`. Anything unparseable lands in `Unsorted/`.

## Projects

```
src/Dropzone.Mtp         cable import: folder parsing, planning, ledger, MediaDevices source
src/Dropzone.LocalSend   protocol v2: discovery, Kestrel receiver, sender
src/Dropzone.App         WPF tray app (H.NotifyIcon)
src/Dropzone.Cli         diagnostics for the cable path
tests/                   xunit
```

## Build and run

The source lives in WSL but targets `net10.0-windows`, so build with the **Windows** SDK:

```powershell
$env:Path = [System.Environment]::GetEnvironmentVariable('Path','Machine') + ';' +
            [System.Environment]::GetEnvironmentVariable('Path','User')
$r = '\\wsl.localhost\Ubuntu-24.04\home\berkc\code\3_FUN\dropzone'

dotnet build $r\Dropzone.slnx
dotnet test  $r\Dropzone.slnx
```

Note it is `Dropzone.slnx`, not `.sln` — .NET 10 emits the XML solution format.

### Diagnostics

```powershell
Dropzone.Cli status            # is a phone connected and unlocked?
Dropzone.Cli scan              # list media the phone exposes
Dropzone.Cli import <folder>   # run a full import
```

## Settings

`%APPDATA%\Dropzone\settings.json` — download folder, photo folder, alias, whether receiving
starts on launch. The import ledger (`imported.txt`) records MTP paths already pulled, so
re-running an import skips them.

## Security notes

- Incoming filenames are untrusted: path components are stripped and the resolved path is
  asserted to stay inside the download folder before any write.
- Upload tokens are compared in fixed time and pinned to the originating IP.
- One transfer session at a time; a second concurrent request gets `409`.
- Peers use self-signed certificates by design — the protocol pins trust to the certificate's
  SHA-256 ("fingerprint") rather than a CA chain.

## Known gaps

- The cable path's `MediaDevicesPhoneSource` has **not** been verified against a real unlocked
  iPhone yet. Everything above it is covered by tests using a fake source.
- No PIN support on incoming transfers, and no per-transfer accept prompt in the UI
  (`ApproveTransfer` exists as a hook, defaulting to accept).
- Remote script execution and the video-editor hook are not built.
