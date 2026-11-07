#!/usr/bin/env bash
# Fetches the binaries the Godot project needs: ZenKit and dmusic (Gothic-Unity's binary-dependencies) and the
# Godot OpenXR vendors plugin. Run once after cloning.
set -euo pipefail
cd "$(dirname "$0")"

BIN_DEPS_REF="${BIN_DEPS_REF:-main}"
VENDORS_VERSION="${VENDORS_VERSION:-5.1.0-stable}"
CACHE="${XDG_CACHE_HOME:-$HOME/.cache}/gothic-godot"
mkdir -p "$CACHE"

if [ ! -d "$CACHE/binary-dependencies" ]; then
    git clone --depth 1 --branch "$BIN_DEPS_REF" https://github.com/Gothic-Unity-Project/binary-dependencies.git \
        "$CACHE/binary-dependencies"
fi
D="$CACHE/binary-dependencies/Core/Dependencies"
mkdir -p lib/linux-x64 lib/android-arm64
cp "$D/ZenKit/ZenKit.dll" "$D/dmusic/DirectMusic.dll" lib/
cp "$D"/ZenKit/LICENSE-* "$D"/dmusic/LICENSE-* lib/
cp "$D/ZenKit/linux_x86/libzenkitcapi.so" "$D/dmusic/linux_x86/libdmusic.so" lib/linux-x64/
cp "$D/ZenKit/libzenkitcapi.so" "$D/dmusic/libdmusic.so" lib/android-arm64/
rm -f lib/*.meta

if [ ! -d addons/godotopenxrvendors ]; then
    curl -sSL -o "$CACHE/vendors.zip" \
        "https://github.com/GodotVR/godot_openxr_vendors/releases/download/$VENDORS_VERSION/godotopenxrvendorsaddon.zip"
    rm -rf "$CACHE/vendors" && mkdir -p "$CACHE/vendors" addons
    unzip -q "$CACHE/vendors.zip" -d "$CACHE/vendors"
    cp -r "$CACHE/vendors/asset/addons/godotopenxrvendors" addons/
fi
# ZenKitCS with our patches (forks/ZenKitCS/*.patch): external calls without garbage. Needs dotnet.
ZENKITCS_REF="${ZENKITCS_REF:-e370d5ea9409ad82d140223eca33ff6bccbf5c59}"
if [ "${USE_ZENKITCS_FORK:-1}" = 1 ] && command -v dotnet >/dev/null; then
    if [ ! -d "$CACHE/ZenKitCS" ]; then
        git clone -q https://github.com/GothicKit/ZenKitCS.git "$CACHE/ZenKitCS"
    fi
    git -C "$CACHE/ZenKitCS" checkout -q -f "$ZENKITCS_REF"
    for patch in ../forks/ZenKitCS/*.patch; do
        git -C "$CACHE/ZenKitCS" apply "$(realpath "$patch")"
    done
    dotnet build -c Release -nologo -v q "$CACHE/ZenKitCS/ZenKit/ZenKit.csproj"
    cp lib/ZenKit.dll lib/ZenKit.upstream.dll
    cp "$CACHE"/ZenKitCS/ZenKit/bin/Release/netstandard2.1/*/ZenKit.dll lib/ZenKit.dll
    git -C "$CACHE/ZenKitCS" checkout -q -f "$ZENKITCS_REF"
fi
echo "Done: lib/ and addons/godotopenxrvendors/"
