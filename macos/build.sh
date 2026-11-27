#!/bin/bash
# 影·筛 macOS 构建脚本：生成 影·筛.app
set -euo pipefail
cd "$(dirname "$0")"

APP_NAME="影·筛"
BUNDLE_NAME="${APP_NAME}.app"
CONFIG="${1:-release}"

echo "==> 编译（${CONFIG}）"
swift build -c "$CONFIG"

echo "==> 组装 ${BUNDLE_NAME}"
rm -rf "$BUNDLE_NAME"
mkdir -p "$BUNDLE_NAME/Contents/MacOS" "$BUNDLE_NAME/Contents/Resources"

cp ".build/${CONFIG}/PhotoCull" "$BUNDLE_NAME/Contents/MacOS/${APP_NAME}"
chmod +x "$BUNDLE_NAME/Contents/MacOS/${APP_NAME}"
cp Info.plist "$BUNDLE_NAME/Contents/Info.plist"
cp Resources/AppIcon.icns "$BUNDLE_NAME/Contents/Resources/AppIcon.icns"

# 文档随包（开源协议与第三方声明要求）
for doc in README.md LICENSE THIRD-PARTY-NOTICES.txt 使用说明.md; do
    if [ -f "$doc" ]; then cp "$doc" "$BUNDLE_NAME/Contents/Resources/$doc"; fi
done

# 临时签名（本机直接运行；分发时请做开发者签名与公证）
codesign --force --deep --sign - "$BUNDLE_NAME" >/dev/null 2>&1 || echo "警告：临时签名失败，仍可直接运行"

echo "==> 完成：$(pwd)/${BUNDLE_NAME}"
echo "    运行：open \"${BUNDLE_NAME}\""
echo "    自检：\"${BUNDLE_NAME}/Contents/MacOS/${APP_NAME}\" --selftest"
