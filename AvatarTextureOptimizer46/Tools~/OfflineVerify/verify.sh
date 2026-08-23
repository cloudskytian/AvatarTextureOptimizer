#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# EN: Offline verification for Avatar Texture Optimizer. Compiles the package against real Unity
#     reference assemblies and the real NDMF sources, then runs the algorithm unit tests.
#     Nothing here ships to users; it exists so a change can be checked without opening Unity.
# ZH: Avatar Texture Optimizer 的离线校验。用真实的 Unity 参考程序集与真实的 NDMF 源码编译本包，
#     然后运行算法单元测试。这些内容不会随包发给用户；它的存在是为了不打开 Unity 也能校验改动。
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
verify="$root/.verify"

command -v dotnet >/dev/null || { echo "dotnet SDK 8 is required: https://dot.net"; exit 1; }

if [ ! -d "$verify/unityrefs/unityengine.modules" ]; then
  echo "== downloading reference assemblies (one time, ~400 MB) =="
  mkdir -p "$verify/unityrefs" "$verify/refs"
  cd "$verify/unityrefs"
  curl -sSL -o um.nupkg "https://api.nuget.org/v3-flatcontainer/unityengine.modules/2021.3.33/unityengine.modules.2021.3.33.nupkg"
  curl -sSL -o u3d.nupkg "https://api.nuget.org/v3-flatcontainer/unity3d.sdk/2021.1.14.1/unity3d.sdk.2021.1.14.1.nupkg"
  mkdir -p unityengine.modules u3d
  (cd unityengine.modules && unzip -qo ../um.nupkg)
  (cd u3d && unzip -qo ../u3d.nupkg)
  for p in "com.unity.mathematics/1.2.6" "com.unity.burst/1.8.7"; do
    n=${p%%/*}; v=${p##*/}
    curl -sSL -o "$n.tgz" "https://packages.unity.com/$n/-/$n-$v.tgz"
    mkdir -p "$n" && tar xzf "$n.tgz" -C "$n" --strip-components=1
  done
  chmod -R u+rwX .
  cd "$verify/refs"
  curl -sSL -o ndmf.zip "https://github.com/bdunderscore/ndmf/releases/download/1.14.4/nadena.dev.ndmf-1.14.4.zip"
  mkdir -p nadena.dev.ndmf-1.14.4 && (cd nadena.dev.ndmf-1.14.4 && unzip -qo ../ndmf.zip)
  chmod -R u+rwX .
fi

echo "== compiling the package =="
out=$(dotnet build "$here/build.csproj" -v q --nologo 2>&1 || true)
echo "$out" | grep -E "(error|warning) CS" | grep "avatar-texture-optimizer" | sort -u || true
n=$(echo "$out" | grep "error CS" | grep -c "avatar-texture-optimizer" || true)
echo "ATO compile errors: $n"
[ "$n" = "0" ] || { echo "COMPILE FAILED"; exit 1; }

echo
echo "== running algorithm tests =="
dotnet run --project "$here/Tests/t.csproj" -v q --nologo
