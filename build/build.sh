#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
runtime="${1:-win-x64}"
case "$runtime" in win-x64|win-arm64) ;; *) printf 'Runtime invalide\n' >&2; exit 1;; esac
dotnet test tests/SwitchPilot.Tests/SwitchPilot.Tests.csproj -c Release --logger 'trx;LogFileName=regression.trx'
dotnet publish src/SwitchPilot.App/SwitchPilot.App.csproj -c Release -r "$runtime" --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false \
  --output "artifacts/$runtime"
cp README.md THIRD-PARTY-NOTICES.md "artifacts/$runtime/"
cp -R docs "artifacts/$runtime/"
(cd "artifacts/$runtime" && sha256sum SwitchPilot.exe > SHA256SUMS.txt)
printf 'Exécutable : artifacts/%s/SwitchPilot.exe\n' "$runtime"
