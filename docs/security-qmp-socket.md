# QMP was reachable from inside the guest

Status: fixed in Weims Manager 1.1.1.

## Affected versions

Weims Manager 1.0 through 1.1.0, and `scripts/start.ps1` up to the same point.

## The problem

QEMU's management interface (QMP) has no authentication. Affected versions started QEMU with

```text
-qmp tcp:127.0.0.1:<port>,server=on,wait=off
```

on the assumption that only programs on the host can connect to `127.0.0.1`. That assumption does not hold with user-mode networking (`-netdev user`): the guest address `10.0.2.2` is forwarded to the host's `127.0.0.1`. Any program inside the macOS guest could therefore open `10.0.2.2:<port>` and talk to QMP.

QMP can stop the VM, and it includes commands that make QEMU read and write files on the host with the permissions of the Windows account that runs it. A program in the guest could use this to act outside the VM.

This was confirmed on 2026-11-19 on one machine: `nc -w 3 10.0.2.2 <port>` in a macOS 26 guest received the QMP greeting, and the host's TCP table showed the connection with both ends owned by the QEMU process. The test only received the greeting; no QMP command was sent.

## The fix

QMP now uses a Unix domain socket:

```text
-qmp unix:<path>,server=on,wait=off
```

- User-mode networking only forwards connections to IP addresses, so the guest cannot reach a socket file.
- The socket lives in `%USERPROFILE%\.weims\q\<12 hex digits>.sock`, with a new name for each start. The folder inherits the profile's permissions, so other accounts on the same PC cannot connect either. Loopback TCP was open to every local account.
- QEMU itself and the downloaded runtime are unchanged. Only the launch arguments differ.

Why that folder:

- Unix socket paths are limited to 108 bytes, so the socket cannot live in a VM folder that the user chose.
- It is deliberately not under `AppData`. When a process is started from a packaged (MSIX) app, Windows redirects `AppData` through a filter, and a socket created there cannot be connected to or even deleted from that process (`WSAEINVAL`, error 1920). The profile root is not redirected.
- If the resulting path is too long, the start fails with a message that names the `WEIMS_QMP_DIR` environment variable. Set it to a short folder that only you can access.

## Compatibility

- A VM must be restarted with the fixed version for the fix to apply. A VM that an older version started keeps listening on TCP until then; the fixed version can still manage it.
- A VM started by the fixed version shows as "Unknown state · Operations locked" in an older Manager. Nothing is changed in that state.
- The `qmp_port` key stays in `config.json` and is still written for new VMs, because older versions validate it. Nothing listens on it. The port field was removed from the Manager's interface.
- Requires Windows 10 version 1803 or later for Unix domain sockets. The installer already requires a later version.

## What is not changed

The guest can still reach other services that listen only on the host's `127.0.0.1`, such as a local development server or a database without a password. That is how QEMU user-mode networking works and is not specific to this project. This fix removes the one such listener that this project created.

## Verification

Run on one Windows 11 machine (build 26300, system code page 936).

| Check | Result |
|---|---|
| `Weims.Core.Tests` | 85 checks pass. They assert that the launch arguments contain no TCP endpoint other than SSH forwarding |
| `Weims.IntegrationTests`, real QEMU with no guest | 18 checks pass: socket QMP works, the QEMU process owns no TCP listener, a VM started with the old TCP argument is still managed, a leftover socket after a hard kill is refused and cleaned up |
| `Weims.UiTests` | 46 checks pass |
| A real macOS 26 guest started by the fixed CLI | Status over the socket returned `Running`; the QEMU process owned one TCP listener, the SSH forward; nothing listened on the old `qmp_port` |
| From inside that guest | A command that tried `10.0.2.2:<old qmp_port>` and then reported the outcome to one of two host listeners reported "not reachable". A control command to a loopback-only host listener did connect, so the guest's path to host loopback was working at the time |
| Guest shutdown, then `stop` through the fixed CLI | Status changed to "Guest shut down", `stop` succeeded, QEMU exited and removed its socket file |
| The 1.1.0 CLI looking at the VM started by the fixed version | "Unknown state · Operations locked", as intended |
| The Manager window (GUI) driving a real guest with the fix | Not verified; only the CLI was used with a real guest |
| Windows 10, and profile paths with characters outside code page 936 | Not verified |

Each real-guest check was run once, on a throwaway overlay of an existing VM.
