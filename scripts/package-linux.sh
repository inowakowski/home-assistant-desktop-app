#!/usr/bin/env bash
#
# Builds HADA's packages for Linux, for x64 and arm64: an AppImage to run as it is, a .deb for Debian and Ubuntu,
# and an .rpm for Fedora and openSUSE. Each holds the service and the app, self-contained, so no .NET runtime is
# needed. Nobody has tried them on Linux by hand yet, so their names say so: HADA-1.9.0-linux-x64-untested.deb
# (see HadaReleases.PackageNames, by which an installed HADA finds its next version).
#
# Runs on Linux, as in CI. Needs dpkg-deb and rpmbuild, and appimagetool given as APPIMAGETOOL.
#
#   scripts/package-linux.sh [output folder]
set -euo pipefail

repo="$(cd "$(dirname "$0")/.." && pwd)"
version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$repo/Directory.Build.props")"
out="${1:-$repo/artifacts/linux}"
appimagetool="${APPIMAGETOOL:?Set APPIMAGETOOL to the path of appimagetool.}"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mkdir -p "$out"

summary="Home Assistant Desktop App: this computer in Home Assistant"
description="HADA reports this computer to Home Assistant, as sensors and controls of a device, over MQTT or
directly, and carries out what Home Assistant asks of it. Not yet tried on Linux by hand."

for arch in x64 arm64; do
    case "$arch" in
        x64) deb_arch=amd64; rpm_arch=x86_64; appimage_arch=x86_64 ;;
        arm64) deb_arch=arm64; rpm_arch=aarch64; appimage_arch=aarch64 ;;
    esac
    name="HADA-$version-linux-$arch-untested"
    stage="$work/$arch/root"

    echo "Publishing HADA for linux-$arch..."
    for project in Service App; do
        dotnet publish "$repo/src/HADA.$project" -c Release -r "linux-$arch" --self-contained \
            -o "$stage/opt/hada/${project,,}" --nologo -v quiet
    done

    # What the desktop knows HADA by: a command, an entry in the menu, an icon.
    install -d "$stage/usr/bin" "$stage/usr/share/applications" "$stage/usr/share/icons/hicolor/256x256/apps"
    ln -s /opt/hada/app/HADA.App "$stage/usr/bin/hada"
    install -m 644 "$repo/assets/hada.png" "$stage/usr/share/icons/hicolor/256x256/apps/hada.png"
    cat > "$stage/usr/share/applications/hada.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=HADA
GenericName=Home Assistant Desktop App
Comment=This computer in Home Assistant
Exec=hada
Icon=hada
Terminal=false
Categories=Utility;
EOF

    echo "Packing $name.deb..."
    deb="$work/$arch/deb"
    cp -a "$stage" "$deb"
    install -d "$deb/DEBIAN"
    cat > "$deb/DEBIAN/control" <<EOF
Package: hada
Version: $version
Architecture: $deb_arch
Maintainer: Ignacy Nowakowski <15818597+inowakowski@users.noreply.github.com>
Section: utils
Priority: optional
Installed-Size: $(du -sk "$stage" | cut -f1)
Depends: libc6, libgcc-s1, libstdc++6, zlib1g, libssl3 | libssl1.1, libicu76 | libicu74 | libicu72 | libicu71 | libicu70 | libicu67, libfontconfig1, libx11-6, libice6, libsm6
Homepage: https://github.com/inowakowski/home-assistant-desktop-app
Description: $summary
$(sed 's/^/ /' <<< "$description")
EOF
    dpkg-deb --root-owner-group --build "$deb" "$out/$name.deb"

    echo "Packing $name.rpm..."
    rpmtop="$work/$arch/rpm"
    mkdir -p "$rpmtop"/{BUILD,RPMS,SPECS,SOURCES,SRPMS}
    cat > "$rpmtop/SPECS/hada.spec" <<EOF
Name: hada
Version: $version
Release: 1
Summary: $summary
License: MIT
URL: https://github.com/inowakowski/home-assistant-desktop-app
AutoReqProv: no
Requires: glibc, libgcc, libstdc++, zlib, openssl-libs, libicu, fontconfig, libX11, libICE, libSM
%global debug_package %{nil}
%global __os_install_post %{nil}

%description
$description

%install
cp -a "$stage/." %{buildroot}/

%files
/opt/hada
/usr/bin/hada
/usr/share/applications/hada.desktop
/usr/share/icons/hicolor/256x256/apps/hada.png
EOF
    # No BuildArch in the spec: rpmbuild refuses one this machine cannot build for, while --target alone packs
    # the files already built for it.
    rpmbuild --quiet --define "_topdir $rpmtop" --target "$rpm_arch" -bb "$rpmtop/SPECS/hada.spec"
    cp "$rpmtop/RPMS/$rpm_arch/"*.rpm "$out/$name.rpm"

    echo "Packing $name.AppImage..."
    appdir="$work/$arch/AppDir"
    cp -a "$stage" "$appdir"
    cp "$repo/assets/hada.png" "$appdir/hada.png"
    cp "$repo/assets/hada.png" "$appdir/.DirIcon"
    cp "$stage/usr/share/applications/hada.desktop" "$appdir/hada.desktop"
    cat > "$appdir/AppRun" <<'EOF'
#!/bin/sh
# The app finds the service in the folder next to its own, inside the image as in a package.
exec "$APPDIR/opt/hada/app/HADA.App" "$@"
EOF
    chmod 755 "$appdir/AppRun"
    ARCH="$appimage_arch" APPIMAGE_EXTRACT_AND_RUN=1 "$appimagetool" --no-appstream "$appdir" "$out/$name.AppImage"
done

ls -l "$out"
