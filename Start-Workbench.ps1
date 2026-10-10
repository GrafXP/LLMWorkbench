param([string]$Project)
$executable = Join-Path $PSScriptRoot 'src\Workbench.Desktop\bin\Release\net10.0-windows\LLMWorkbench.exe'
if (-not (Test-Path -LiteralPath $executable)) {
    dotnet build (Join-Path $PSScriptRoot 'LLMWorkbench.slnx') -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Workbench build failed.' }
}
if ($Project) { & $executable --project $Project } else { & $executable }
