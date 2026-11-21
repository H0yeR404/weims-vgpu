# Prebuilt Guest Tools install disk

`weims-guest-tools.img` is the install disk that Weims Manager plugs into a running VM when the user
clicks **Install Guest Tools**. It is a raw 8 MiB disk image with one HFS+ volume that holds
`Install Weims Guest Tools.app` (the helper, which installs itself when started under that name) and
`Uninstall Weims Guest Tools.command`.

`weims-guest-tools-helper-<version>.tar` is the helper alone. The broker sends it to a helper in a
guest that runs an older version; that helper installs it only if it is signed like itself.

Both are kept here because they can only be built on macOS, while Manager is built on Windows.
`manager/build.ps1` checks every file named in `SHA256SUMS` and copies it next to Manager.

The image is the same for every VM. What is specific to a VM, its one-time pairing code, is given to
the disk as its USB serial number when it is plugged in; nothing is written into the image.

## Rebuilding

In a macOS guest with the Xcode command line tools, from the `guest-tools` folder:

```sh
sh make-identity.sh   # once per build Mac: creates the signing identity
sh make-image.sh      # builds build/weims-guest-tools.img and the DMG
```

Copy `build/weims-guest-tools.img` and `build/weims-guest-tools-helper-<version>.tar` here, remove the
archive of the previous version, and put their two lines from `build/SHA256SUMS` into `SHA256SUMS`.
Two builds of the same sources are not byte-identical: the image carries timestamps and a signature.

## Signature

The app in this image is signed with the project's self-signed certificate "Weims Guest Tools Signing",
SHA-256 fingerprint
`A2:88:2F:B0:AC:93:35:84:04:2C:43:FD:86:50:D9:B4:68:73:A7:A4:B2:CD:27:A7:2B:68:EA:11:AE:77:15:BA`.
The certificate itself (public, no key) is `../signing-certificate.pem`. To check an installed helper
against it in macOS:

```sh
codesign -d -r- "$HOME/Applications/Weims Guest Tools.app"   # prints: ... certificate leaf = H"9d79eabb...2609623"
openssl x509 -in signing-certificate.pem -noout -fingerprint -sha1   # the same 40 hex digits
```

The private key is not in this repository and must never be.

The certificate is not issued by Apple, so Gatekeeper treats the app like any other app from outside
the App Store. The signature exists so that macOS recognises later builds as the same app and keeps
what the user allowed. Builds signed with another key are a different app to macOS.
