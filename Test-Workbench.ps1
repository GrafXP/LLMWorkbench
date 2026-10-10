$ErrorActionPreference = 'Stop'
Push-Location -LiteralPath $PSScriptRoot
try {
    dotnet build LLMWorkbench.slnx -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    dotnet test LLMWorkbench.slnx -c Release --no-build --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    $smokeDirectory = Join-Path $PSScriptRoot ('local-data\verification-' + [guid]::NewGuid().ToString('N'))
    $executable = Join-Path $PSScriptRoot 'src\Workbench.Desktop\bin\Release\net10.0-windows\LLMWorkbench.exe'
    $arguments = '--smoke-test "' + $smokeDirectory + '"'
    $process = Start-Process -FilePath $executable -ArgumentList $arguments -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(25000)) {
        Stop-Process -Id $process.Id -ErrorAction SilentlyContinue
        throw 'Desktop verification timed out.'
    }
    $errorFile = Join-Path $smokeDirectory 'error.txt'
    if ($process.ExitCode -ne 0 -or (Test-Path -LiteralPath $errorFile)) {
        if (Test-Path -LiteralPath $errorFile) { Get-Content -LiteralPath $errorFile }
        throw 'Desktop verification failed.'
    }
    $report = Get-Content -Raw -LiteralPath (Join-Path $smokeDirectory 'result.json') | ConvertFrom-Json
    if ($report.Jobs.Count -ne 4 -or ($report.Jobs | Where-Object State -NE 'Done')) { throw 'The desktop example did not complete.' }
    Write-Output ('Desktop verification passed. Screenshot and reports: ' + $smokeDirectory)
} finally { Pop-Location }
