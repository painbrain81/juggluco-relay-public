# Builds the folder to copy to the server: publish/package
#   powershell -ExecutionPolicy Bypass -File deploy\package.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'publish\package'

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish (Join-Path $root 'src\JugglucoRelay') -c Release -r linux-x64 --self-contained `
    -p:PublishSingleFile=true -p:DebugType=none -o (Join-Path $root 'publish\linux-x64')
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

New-Item -ItemType Directory $out | Out-Null
Copy-Item (Join-Path $root 'publish\linux-x64\JugglucoRelay') $out
# Scripts and units need Unix line endings
foreach ($f in 'install.sh', 'compare.sh', 'juggluco-relay.service', 'appsettings.server.json', 'journal.py') {
    $text = [IO.File]::ReadAllText((Join-Path $PSScriptRoot $f)) -replace "`r`n", "`n"
    [IO.File]::WriteAllText((Join-Path $out $f), $text, (New-Object Text.UTF8Encoding $false))
}
Write-Host "Ready: $out"
Write-Host "Copy it to the server with:  scp -r `"$out`" <user>@<server>:~/juggluco-relay"
