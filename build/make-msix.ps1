<#
  make-msix.ps1 — build the Microsoft Store package for CertConvert.

  Run on WINDOWS (needs the Windows SDK's MakeAppx.exe). From the repo root:
      pwsh build/make-msix.ps1
  Produces artifacts/msix/CertConvert-<version>.msix — an UNSIGNED store
  package. Do NOT sign it: the Microsoft Store re-signs on submission, and the
  Identity here is already the Store-assigned one.

  Upload the .msix at Partner Center → CertConvert → Submissions → Packages, or
  via the Store submission API.

  The store variant (-p:StoreBuild=true) strips the self-updater and Ko-fi
  links — the Store delivers updates, and store customers have already paid.
#>
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

$project = 'src/CertConvert/CertConvert.csproj'
$version = (Select-String -Path $project -Pattern '<Version>([^<]+)').Matches[0].Groups[1].Value
$msixVersion = "$version.0"   # Store requires a 4-part version, revision 0

$out = 'artifacts/msix'
$layout = "$out/layout"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Path $layout | Out-Null

Write-Host "==> Publishing win-x64 (store variant)"
dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:DebugType=none -p:StoreBuild=true `
    --artifacts-path "$out/int" -o $layout
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Write-Host "==> Assembling package layout"
Copy-Item 'build/msix/assets' (Join-Path $layout 'assets') -Recurse -Force
# Stamp the manifest version and drop it into the layout root.
$manifest = Get-Content 'build/msix/AppxManifest.xml' -Raw
# (?<![A-Za-z]) keeps this off MinVersion/MaxVersionTested — only the bare
# Version attribute on <Identity> may be stamped.
$manifest = $manifest -replace '(?<![A-Za-z])Version="[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+"', "Version=`"$msixVersion`""
Set-Content (Join-Path $layout 'AppxManifest.xml') $manifest -Encoding UTF8

Write-Host "==> Locating MakeAppx.exe"
$makeappx = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Recurse -Filter 'makeappx.exe' -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match 'x64' } | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $makeappx) { throw "MakeAppx.exe not found — install the Windows 10/11 SDK." }

$pkg = "$out/CertConvert-$version.msix"
Write-Host "==> Packing $pkg"
& $makeappx.FullName pack /d $layout /p $pkg /o
if ($LASTEXITCODE -ne 0) { throw "MakeAppx pack failed" }

Write-Host ""
Write-Host "Done: $pkg (version $msixVersion, unsigned — the Store signs on submission)."
Write-Host "Upload at Partner Center → CertConvert → Submissions → Packages."
