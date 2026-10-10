param([string]$Resume)
$ErrorActionPreference = 'Stop'
Push-Location -LiteralPath $PSScriptRoot
try {
    if ($Resume) { dotnet run --project tools\Workbench.LiveCheck -c Release -- $PSScriptRoot $Resume }
    else { dotnet run --project tools\Workbench.LiveCheck -c Release -- $PSScriptRoot }
    if ($LASTEXITCODE -ne 0) { throw 'Phase 2 live verification failed.' }
} finally { Pop-Location }
