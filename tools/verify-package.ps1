# verify-package.ps1 -- verify a MultiplayerStability package (zip or installed folder).
# Usage:
#   powershell -File tools\verify-package.ps1 -Path <MultiplayerStability.zip | installed folder>
#             [-ExpectedVersion 0.8.32] [-ExpectedZipSha <hash>] [-ExpectedDllSha <hash>]
# Checks: manifest version, DLL presence, required folders (incl. Blueprints), optional SHA-256s,
# and DLL freshness relative to the newest source file when run from the repo.
param(
    [Parameter(Mandatory = $true)][string]$Path,
    [string]$ExpectedVersion = "0.8.32",
    [string]$ExpectedZipSha = "",
    [string]$ExpectedDllSha = ""
)
$fail = 0
function Fail($msg) { Write-Host "FAIL  $msg" -ForegroundColor Red; $script:fail++ }
function Ok($msg)   { Write-Host "ok    $msg" }

$isZip = $Path.ToLower().EndsWith(".zip")
$work = $Path
if ($isZip) {
    if (-not (Test-Path $Path)) { Fail "package not found: $Path"; exit 1 }
    if ($ExpectedZipSha) {
        $h = (Get-FileHash $Path -Algorithm SHA256).Hash
        if ($h -eq $ExpectedZipSha) { Ok "zip SHA-256 matches" } else { Fail "zip SHA-256 $h != expected $ExpectedZipSha" }
    }
    $work = Join-Path $env:TEMP ("mpsverify_" + [IO.Path]::GetRandomFileName())
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::ExtractToDirectory($Path, $work)
}

$manifest = Join-Path $work "OwlcatModificationManifest.json"
if (-not (Test-Path $manifest)) { Fail "OwlcatModificationManifest.json missing" }
else {
    $v = (Get-Content $manifest -Raw | Select-String '"Version"\s*:\s*"([^"]+)"').Matches[0].Groups[1].Value
    if ($v -eq $ExpectedVersion) { Ok "manifest Version = $v" } else { Fail "manifest Version '$v' != expected '$ExpectedVersion'" }
}

$dll = Join-Path $work "Assemblies\MultiplayerStability.dll"
if (-not (Test-Path $dll)) { Fail "Assemblies\MultiplayerStability.dll missing" }
else {
    $d = Get-Item $dll
    Ok ("DLL present: {0} bytes, {1}" -f $d.Length, $d.LastWriteTime)
    if ($ExpectedDllSha) {
        $h = (Get-FileHash $dll -Algorithm SHA256).Hash
        if ($h -eq $ExpectedDllSha) { Ok "DLL SHA-256 matches" } else { Fail "DLL SHA-256 $h != expected $ExpectedDllSha" }
    }
    # Freshness vs sources when run from within the repository checkout.
    $repoScripts = Join-Path $PSScriptRoot "..\Scripts"
    if (Test-Path $repoScripts) {
        $newest = Get-ChildItem $repoScripts -Filter *.cs | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($newest -and $newest.LastWriteTime -gt $d.LastWriteTime) {
            Fail ("DLL is OLDER than source {0} ({1} > {2}) -- stale package" -f $newest.Name, $newest.LastWriteTime, $d.LastWriteTime)
        } else { Ok "DLL not older than newest source file" }
    }
}

foreach ($req in @("Blueprints", "Bundles", "Localization")) {
    if (Test-Path (Join-Path $work $req)) { Ok "$req/ present" } else { Fail "$req/ missing (Blueprints absence causes a loader exception)" }
}

if ($isZip -and (Test-Path $work)) { Remove-Item $work -Recurse -Force -Confirm:$false }
if ($fail -eq 0) { Write-Host "`nPACKAGE OK" -ForegroundColor Green; exit 0 }
else { Write-Host ("`n{0} FAILURE(S)" -f $fail) -ForegroundColor Red; exit 1 }
