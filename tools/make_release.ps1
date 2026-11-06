# Builds the AnimIT release zip: a self-contained win-x64 build (no .NET install needed) + a portable Python for
# the animation porter.
#   .\tools\make_release.ps1 -Python <portable Python 3.12 folder with numpy + Pillow in Lib\site-packages>
param([Parameter(Mandatory = $true)][string]$Python)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
[xml]$proj = Get-Content (Join-Path $root 'app\AnimIT.csproj')
$version = $proj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$name = "AnimIT-$version-win-x64"
$dist = Join-Path $root "release\$name"
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }

if (-not (Test-Path (Join-Path $Python 'python.exe'))) { throw "No python.exe in $Python" }
& (Join-Path $Python 'python.exe') -c "import numpy, PIL; print('python ok:', numpy.__version__, PIL.__version__)"
if ($LASTEXITCODE) { throw 'The portable Python cannot import numpy and Pillow.' }

dotnet publish (Join-Path $root 'app\AnimIT.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:DebugType=none -o $dist
if ($LASTEXITCODE) { throw 'dotnet publish failed' }

# the porter's Python, with no caches
Copy-Item $Python (Join-Path $dist 'Porter\python') -Recurse
Get-ChildItem $dist -Recurse -Directory -Filter '__pycache__' | Remove-Item -Recurse -Force

# never ship proprietary or game files
$bad = Get-ChildItem $dist -Recurse -File | Where-Object { $_.Name -like 'oo2core*' -or $_.Extension -in '.pac', '.cak', '.clips', '.utoc', '.ucas' }
if ($bad) { throw "Refusing to package: $($bad.FullName -join ', ')" }

Copy-Item (Join-Path $root 'LICENSE'), (Join-Path $root 'THIRD-PARTY-NOTICES.md'), (Join-Path $root 'README.md') $dist
New-Item -ItemType Directory -Force (Join-Path $dist 'docs') | Out-Null
Copy-Item (Join-Path $root 'docs\*.md') (Join-Path $dist 'docs')
Copy-Item (Join-Path $root 'docs\images') (Join-Path $dist 'docs\images') -Recurse

$zip = Join-Path $root "release\$name.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $dist -DestinationPath $zip
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash
"$hash  $name.zip" | Set-Content (Join-Path $root "release\$name.zip.sha256")
"release: $zip"
"sha256:  $hash"
