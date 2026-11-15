#!/usr/bin/env sh
# Build the native notifier and pack it for the Shard bundle.
#
# A Shard bundle is text-only, so the .NET assembly is gzipped and base64'd into
# plugin/notify.dll.gz.b64 (the same trick as the 8BitDo Guide Shard's
# guide-bridge.dll.gz.b64). The runtimeconfig.json ships as plain text beside it.
#
# Needs the .NET SDK with the netcoreapp3.1 targeting pack (the TV runs .NET Core 3.1).
set -e

root=$(cd "$(dirname "$0")/.." && pwd)
plugin="$root/popup-notifications"
publish="$root/native/bin/publish"

rm -rf "$publish"
dotnet publish "$root/native/Notify.csproj" -c Release -o "$publish"

cp "$publish/notify.runtimeconfig.json" "$plugin/notify.runtimeconfig.json"
if [ -f "$publish/notify.deps.json" ]; then cp "$publish/notify.deps.json" "$plugin/notify.deps.json"; fi

# One line, whatever the platform's base64 does with wrapping: the Shard reads the
# file whole and hands it to `base64 -d`.
gzip -9 -c "$publish/notify.dll" | base64 | tr -d '\n' > "$plugin/notify.dll.gz.b64"
printf '\n' >> "$plugin/notify.dll.gz.b64"

printf 'wrote %s (%s bytes)\n' "plugin/notify.dll.gz.b64" "$(wc -c < "$plugin/notify.dll.gz.b64" | tr -d ' ')"
printf 'next: pack plugin/ with Vetro'"'"'s scripts/vetro-plugin.cjs pack\n'
