#!/bin/sh
# SPDX-License-Identifier: GPL-2.0-or-later
# Creates the self-signed identity that release builds of Weims Guest Tools are signed with, in a
# keychain of its own. Run once on the build Mac.
#
# Why: macOS remembers what the user allowed (Desktop, Documents, Downloads access) per signing
# identity. An ad-hoc signature changes with every build, so every update would ask again; a
# certificate that stays the same does not. The certificate does not have to be trusted by the
# system for this, and Gatekeeper treats the app like any other app from outside the App Store.
#
# The private key exists only in that keychain. Back the keychain file and its password file up:
# a build signed with another key is a different app to macOS, and installed helpers refuse an
# update that is not signed with the key they were signed with.
set -eu
NAME="${WEIMS_SIGN_IDENTITY:-Weims Guest Tools Signing}"
KEYCHAIN="$HOME/Library/Keychains/weims-guest-tools-signing.keychain-db"
SECRET="$HOME/Library/Application Support/Weims Guest Tools Build"
# The system's LibreSSL writes a PKCS#12 file that `security import` reads; OpenSSL 3 from Homebrew may not.
OPENSSL=/usr/bin/openssl

if security find-identity -p codesigning "$KEYCHAIN" 2>/dev/null | grep -q "\"$NAME\""; then
  echo "The identity \"$NAME\" already exists in $KEYCHAIN."
  exit 0
fi
umask 077
mkdir -p "$SECRET"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
PASSWORD="$("$OPENSSL" rand -hex 24)"
printf '%s' "$PASSWORD" > "$SECRET/keychain-password"
cat > "$WORK/request.cnf" <<EOF
[req]
distinguished_name=dn
x509_extensions=v3
prompt=no
[dn]
CN=$NAME
[v3]
basicConstraints=critical,CA:false
keyUsage=critical,digitalSignature
extendedKeyUsage=critical,codeSigning
EOF
"$OPENSSL" req -x509 -newkey rsa:3072 -nodes -days 7300 -config "$WORK/request.cnf" -keyout "$WORK/key.pem" -out "$WORK/certificate.pem" 2>/dev/null
"$OPENSSL" pkcs12 -export -inkey "$WORK/key.pem" -in "$WORK/certificate.pem" -name "$NAME" -out "$WORK/identity.p12" -passout "pass:$PASSWORD"
security create-keychain -p "$PASSWORD" "$KEYCHAIN"
security set-keychain-settings "$KEYCHAIN"
security unlock-keychain -p "$PASSWORD" "$KEYCHAIN"
security import "$WORK/identity.p12" -k "$KEYCHAIN" -P "$PASSWORD" -T /usr/bin/codesign > /dev/null
# Lets codesign use the key without a prompt.
security set-key-partition-list -S apple-tool:,apple: -s -k "$PASSWORD" "$KEYCHAIN" > /dev/null 2>&1
# Adds the keychain to this user's search list, keeping what is there.
security list-keychains -d user -s "$KEYCHAIN" $(security list-keychains -d user | tr -d '"' | grep -v weims-guest-tools-signing)
cp "$WORK/certificate.pem" "$SECRET/certificate.pem"
echo "Created \"$NAME\"."
"$OPENSSL" x509 -in "$SECRET/certificate.pem" -noout -fingerprint -sha256 -enddate
echo "Back up: $KEYCHAIN"
echo "    and: $SECRET/keychain-password"
