param([Parameter(Mandatory = $true)][string]$Project)
$ErrorActionPreference = 'Stop'
$projectPath = (Resolve-Path -LiteralPath $Project).Path
if (-not (Test-Path -LiteralPath (Join-Path $projectPath 'project.sqlite'))) { throw 'Choose a completed phase 2 live-check project directory.' }
$reportPath = Join-Path $PSScriptRoot ('local-data\phase2-desktop-' + [guid]::NewGuid().ToString('N'))
$executable = Join-Path $PSScriptRoot 'src\Workbench.Desktop\bin\Release\net10.0-windows\LLMWorkbench.exe'
$arguments = '--project "' + $projectPath + '" --phase2-smoke "' + $reportPath + '"'
$process = Start-Process -FilePath $executable -ArgumentList $arguments -WindowStyle Hidden -PassThru
if (-not $process.WaitForExit(25000)) { Stop-Process -Id $process.Id -ErrorAction SilentlyContinue; throw 'Phase 2 desktop check timed out.' }
$errorFile = Join-Path $reportPath 'error.txt'
if ($process.ExitCode -ne 0 -or (Test-Path -LiteralPath $errorFile)) { if (Test-Path -LiteralPath $errorFile) { Get-Content -LiteralPath $errorFile }; throw 'Phase 2 desktop verification failed.' }
Write-Output ('Phase 2 desktop verification passed. Screenshot and report: ' + $reportPath)
