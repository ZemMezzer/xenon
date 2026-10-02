#!/bin/sh
set -eu

if [ "$#" -ne 4 ]; then
    echo "Usage: PackageMacApp.sh icon.png publish-directory work-directory version" >&2
    exit 1
fi

icon=$1
publish=$2
work=$3
version=$4
bundle="$publish/Xenon.app"
iconset="$work/Xenon.iconset"

if [ ! -f "$icon" ] || [ ! -f "$publish/xenon" ]; then
    echo "The source icon and published NativeAOT executable are required." >&2
    exit 1
fi

# CFBundleVersion and CFBundleShortVersionString contain numeric components.
# Development versions such as 0.1.0-dev use their numeric base in the bundle.
bundle_version=${version%%[-+]*}
case "$bundle_version" in
    ''|*[!0-9.]*) echo "Invalid numeric application version: $version" >&2; exit 1 ;;
esac

mkdir -p "$iconset" "$bundle/Contents/MacOS" "$bundle/Contents/Resources"
for size in 16 32 128 256 512; do
    /usr/bin/sips -z "$size" "$size" "$icon" --out "$iconset/icon_${size}x${size}.png" >/dev/null
    doubled=$((size * 2))
    /usr/bin/sips -z "$doubled" "$doubled" "$icon" --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
done
/usr/bin/iconutil -c icns "$iconset" -o "$bundle/Contents/Resources/Xenon.icns"

# The .app is the distribution: its executable remains usable through PATH.
cp "$publish/xenon" "$bundle/Contents/MacOS/xenon"
chmod 755 "$bundle/Contents/MacOS/xenon"
if [ -f "$publish/LICENSE" ]; then
    cp "$publish/LICENSE" "$bundle/Contents/Resources/LICENSE"
fi
cat > "$bundle/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleDevelopmentRegion</key><string>en</string>
    <key>CFBundleIdentifier</key><string>com.xenonlang.compiler</string>
    <key>CFBundleName</key><string>Xenon</string>
    <key>CFBundleDisplayName</key><string>Xenon</string>
    <key>CFBundleExecutable</key><string>xenon</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
    <key>CFBundleIconFile</key><string>Xenon.icns</string>
    <key>CFBundleShortVersionString</key><string>$bundle_version</string>
    <key>CFBundleVersion</key><string>$bundle_version</string>
    <key>LSUIElement</key><true/>
    <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
EOF

/usr/bin/plutil -lint "$bundle/Contents/Info.plist"
# Seal the icon and plist with the executable. This is a local ad-hoc signature,
# not Developer ID signing or notarization.
/usr/bin/codesign --force --sign - "$bundle"
/usr/bin/codesign --verify --strict "$bundle"
rm "$publish/xenon"
echo "Created $bundle"
