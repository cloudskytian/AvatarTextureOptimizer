#!/bin/sh
# Package the deliverable zip. / 打包交付 zip。
set -e
cd "$(dirname "$0")/.."
VERSION=$(python3 -c "import json;print(json.load(open('package.json'))['version'])" 2>/dev/null || echo dev)
OUT="avatar-texture-optimizer-${VERSION}.zip"
rm -f "$OUT"
zip -r "$OUT" package.json LICENSE README.md CLAUDE.md Runtime Editor docs tools -x "*.pyc" -x "*__pycache__*"
echo "created $OUT"
