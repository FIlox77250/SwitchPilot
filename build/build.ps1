param(
    [ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64',
    [string]$Version = '1.0.3',
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$Output = Join-Path $Root "artifacts/$Runtime"
Push-Location $Root
try {
    if (!(Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Installez le SDK .NET 10.0.401 ou un correctif plus récent de cette bande.' }
    if (!$SkipTests) {
        dotnet test tests/SwitchPilot.Tests/SwitchPilot.Tests.csproj -c Release --logger 'trx;LogFileName=regression.trx'
        if ($LASTEXITCODE -ne 0) { throw 'Les tests ont échoué.' }
    }
    dotnet publish src/SwitchPilot.App/SwitchPilot.App.csproj -c Release -r $Runtime --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -p:Version=$Version --output $Output
    if ($LASTEXITCODE -ne 0) { throw 'La publication a échoué.' }
    $Exe = Join-Path $Output 'SwitchPilot.exe'
    if (!(Test-Path $Exe)) { throw 'Exécutable absent.' }
    $Hash = (Get-FileHash $Exe -Algorithm SHA256).Hash.ToLowerInvariant()
    "$Hash  SwitchPilot.exe" | Set-Content (Join-Path $Output 'SHA256SUMS.txt') -Encoding ascii
    Copy-Item README.md, THIRD-PARTY-NOTICES.md, CHANGELOG.md -Destination $Output
    Copy-Item docs -Destination $Output -Recurse -Force
    Write-Host "Prêt : $Exe"
    Write-Host ('Taille : {0:N1} Mo' -f ((Get-Item $Exe).Length / 1MB))
    Write-Host "SHA256 : $Hash"
} finally { Pop-Location }
