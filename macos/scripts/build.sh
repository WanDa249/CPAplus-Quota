#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/.."
export MACOSX_DEPLOYMENT_TARGET=13.0
mkdir -p dist
for arch in arm64 x86_64; do
  swift build -c release --arch "$arch" --scratch-path ".build-$arch"
  binary_dir=$(swift build -c release --arch "$arch" --scratch-path ".build-$arch" --show-bin-path)
  cp "$binary_dir/CPAplusQuota" "dist/CPAplusQuota-$arch"
done
app="dist/CPAplusQuota.app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
lipo -create dist/CPAplusQuota-arm64 dist/CPAplusQuota-x86_64 -output "$app/Contents/MacOS/CPAplusQuota"
cp Resources/Info.plist "$app/Contents/Info.plist"
cp README.md "$app/Contents/Resources/Mac-Beta-README.md"
cp ../LICENSE.txt "$app/Contents/Resources/LICENSE.txt"
# Local ad-hoc signature is required for arm64 execution. No Developer ID, no notarization.
codesign --force --sign - --identifier io.github.WanDa249.CPAplusQuota "$app"
codesign --verify --strict --verbose=2 "$app"
lipo -archs "$app/Contents/MacOS/CPAplusQuota"
file "$app/Contents/MacOS/CPAplusQuota"
plutil -lint "$app/Contents/Info.plist"
ditto -c -k --sequesterRsrc --keepParent "$app" dist/CPAplusQuota-0.1.1-beta-macos-universal-UNSIGNED.zip
(cd dist && shasum -a 256 CPAplusQuota-0.1.1-beta-macos-universal-UNSIGNED.zip > SHA256SUMS.txt)
