$ErrorActionPreference = 'Stop'

$dotnetPath = Join-Path $PSScriptRoot '.dotnet8\dotnet.exe'
$dotnetCommand = if (Test-Path -LiteralPath $dotnetPath) { $dotnetPath } else { 'dotnet' }

$publishDirectory = Join-Path $PSScriptRoot "dist-final"
if (Test-Path -LiteralPath $publishDirectory) {
  Remove-Item -LiteralPath $publishDirectory -Recurse -Force
}

& $dotnetCommand publish "$PSScriptRoot\MabiLifeAssistant.csproj" `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o $publishDirectory

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$runtimeOutput = Join-Path $PSScriptRoot "bin\Release\net8.0-windows\win-x64"
foreach ($architecture in @("x64", "x86")) {
  $nativeSource = Join-Path $runtimeOutput $architecture
  $nativeDestination = Join-Path $publishDirectory $architecture
  if (Test-Path -LiteralPath $nativeSource) {
    Copy-Item -LiteralPath $nativeSource -Destination $nativeDestination -Recurse -Force
  }
}

$ocrModelSource = Join-Path $PSScriptRoot "models\v6"
$ocrModelDestination = Join-Path $publishDirectory "models\v6"
New-Item -ItemType Directory -Path $ocrModelDestination -Force | Out-Null
Copy-Item -Path (Join-Path $ocrModelSource '*') -Destination $ocrModelDestination -Recurse -Force

Write-Host "Created $publishDirectory\MabiLifeAssistant.exe"
