# SMBLibrary in Weims

Weims.Broker serves a VM's shared folders to the macOS guest with [SMBLibrary](https://github.com/TalAloni/SMBLibrary),
an SMB server and client library by Tal Aloni, licensed under LGPL-3.0-or-later ([License.txt](License.txt); the GNU GPL
it refers to is in the repository's `licenses/GPL-3.0-or-later.txt`).

## What is here

| Folder | Content |
|---|---|
| `SMBLibrary/`, `Utilities/`, `SMBLibrary.Win32/` | Upstream's source at commit `582635541d2ec0c84eac5f66cbebd41e2d35c7ae` ("SMBLibrary 1.5.8.1", 2026-09-20), with **one file changed** (below); every other file is byte-identical to that commit. Upstream's tests, its server application and `SMBLibrary.Adapters` are not copied. |
| `projects/` | Weims' two project files, which build those sources for .NET 10 as `SMBLibrary.dll` (with `Utilities`, as upstream's release packages it) and `SMBLibrary.Win32.dll`. |

To check the copy against upstream (the only difference reported is `SMBLibrary/Server/SMBServer.cs`):

```bash
git clone https://github.com/TalAloni/SMBLibrary && git -C SMBLibrary checkout 582635541d2ec0c84eac5f66cbebd41e2d35c7ae
diff -r SMBLibrary/SMBLibrary manager/third_party/SMBLibrary/SMBLibrary
diff -r SMBLibrary/Utilities manager/third_party/SMBLibrary/Utilities
diff -r SMBLibrary/SMBLibrary.Win32 manager/third_party/SMBLibrary/SMBLibrary.Win32
```

## The change to the source

`SMBLibrary/Server/SMBServer.cs`, changed by Weims on 2026-11-24, marked in the file: `ConnectRequestCallback` accepts a
connection and then calls `BeginAccept` on the listener again. When `Stop()` has closed the listener in between, that
call throws `ObjectDisposedException` on a thread pool thread, which ends the process. The broker stops and starts the
server while it runs (a helper leaves, the list of folders changes), so both such calls are now inside a `try` that
ignores that exception. Found by the broker's tests, where a connection arrived just before a stop.

## How Weims' build differs from upstream's release

The other differences are in `projects/`:

- It targets .NET 10 instead of .NET Framework 2.0, 4.0 and .NET Standard 2.0. .NET 10 has an `AesCcm` class of its own,
  so the project says that the name means upstream's `Utilities.AesCcm`, as it does in upstream's builds.
- `ENABLE_NTLMV1` is not defined: the server accepts NTLMv2 and nothing older.
- Compiler warnings are switched off: the code is upstream's, and its warnings are not acted on here.

Weims needs two more things the library does not offer as public settings. Both are done outside the library, in
`manager/Weims.Broker/ShareServer.cs` and `GuardedStore.cs`:

- **A port other than 445.** The server's `Start` overload that takes a port is `protected internal`; the broker calls it
  from a class derived from `SMBServer`.
- **A limit on how fast a guest may write.** The server handles a connection's requests one after another on the thread
  that reads them, so a write that waits in the broker's file store keeps the server from reading more.

## Replacing the library

The two assemblies are shipped as separate files beside `Weims.Broker.exe`. To run the broker with a changed SMBLibrary,
edit the sources here, build `projects/SMBLibrary.Win32/SMBLibrary.Win32.csproj` with the .NET 10 SDK and replace
`SMBLibrary.dll` and `SMBLibrary.Win32.dll` in Manager's folder.
