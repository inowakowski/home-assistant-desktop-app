#!/usr/bin/env bash
#
# Builds HADA for macOS, for Apple silicon (arm64) and Intel (x64): HADA.app, each in a disk image to drag into
# Applications. The app holds the window and its agent in Contents/MacOS, and the service in
# Contents/MacOS/service, self-contained, so no .NET runtime is needed. Nobody has tried them on a Mac by hand yet,
# so their names say so: HADA-1.11.0-macos-arm64-untested.dmg (see HadaReleases.PackageNames, by which an installed
# HADA finds its next version).
#
# The app is signed ad hoc, which Apple silicon needs to run it at all, and not with a developer's certificate: the
# first time, macOS asks for it to be allowed in System Settings, under Privacy & Security.
#
# Runs on macOS, as in CI. Needs sips, iconutil, codesign and hdiutil, which every Mac has.
#
#   scripts/package-macos.sh [output folder]
set -euo pipefail

repo="$(cd "$(dirname "$0")/.." && pwd)"
version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$repo/Directory.Build.props")"
out="${1:-$repo/artifacts/macos}"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mkdir -p "$out"

# The icon, in the sizes the Finder and the Dock ask for, from the one picture there is.
iconset="$work/hada.iconset"
mkdir -p "$iconset"
for size in 16 32 128 256 512; do
    sips -z "$size" "$size" "$repo/assets/hada.png" --out "$iconset/icon_${size}x${size}.png" > /dev/null
    double=$((size * 2))
    sips -z "$double" "$double" "$repo/assets/hada.png" --out "$iconset/icon_${size}x${size}@2x.png" > /dev/null
done
iconutil -c icns "$iconset" -o "$work/hada.icns"

for arch in arm64 x64; do
    name="HADA-$version-macos-$arch-untested"
    app="$work/$arch/HADA.app"

    echo "Publishing HADA for osx-$arch..."
    dotnet publish "$repo/src/HADA.App" -c Release -r "osx-$arch" --self-contained -o "$app/Contents/MacOS" --nologo -v quiet
    dotnet publish "$repo/src/HADA.Service" -c Release -r "osx-$arch" --self-contained -o "$app/Contents/MacOS/service" --nologo -v quiet

    install -d "$app/Contents/Resources"
    cp "$work/hada.icns" "$app/Contents/Resources/hada.icns"

    # An app of the menu bar: no icon in the Dock, the window opened from the menu bar's icon. It shuts the Mac down
    # and restarts it by asking the login window, which macOS lets it do once allowed under Automation.
    cat > "$app/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>HADA</string>
    <key>CFBundleDisplayName</key>
    <string>HADA</string>
    <key>CFBundleIdentifier</key>
    <string>io.github.inowakowski.hada</string>
    <key>CFBundleVersion</key>
    <string>$version</string>
    <key>CFBundleShortVersionString</key>
    <string>$version</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleExecutable</key>
    <string>HADA.App</string>
    <key>CFBundleIconFile</key>
    <string>hada</string>
    <key>LSMinimumSystemVersion</key>
    <string>14.0</string>
    <key>LSUIElement</key>
    <true/>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>NSAppleEventsUsageDescription</key>
    <string>HADA asks the login window to shut the Mac down or restart it when Home Assistant tells it to.</string>
    <key>NSHumanReadableCopyright</key>
    <string>Ignacy Nowakowski, MIT License</string>
</dict>
</plist>
EOF

    # Every file in Contents/MacOS is code to macOS, and is signed; then the app around them, which signs its main
    # program last. Signed before the others, the main program would be taken for the app with unsigned files in it.
    echo "Signing HADA.app for $arch..."
    find "$app/Contents/MacOS" -type f ! -path "$app/Contents/MacOS/HADA.App" -print0 | xargs -0 codesign --force --sign - --timestamp=none
    codesign --force --sign - --timestamp=none "$app"
    codesign --verify --deep --strict "$app"

    echo "Packing $name.dmg..."
    stage="$work/$arch/dmg"
    mkdir -p "$stage"
    cp -R "$app" "$stage/"
    ln -s /Applications "$stage/Applications"
    hdiutil create -quiet -volname "HADA $version" -srcfolder "$stage" -ov -format UDZO "$out/$name.dmg"
done

ls -l "$out"
