#requires -Version 7.0
# Pure helper regression tests. No harness process or dotnet executable is invoked.
$ErrorActionPreference = 'Stop'
$tokens = $null
$errors = $null
$entry = Join-Path $PSScriptRoot 'Test-WindowsInterruption.ps1'
$ast = [Management.Automation.Language.Parser]::ParseFile($entry, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
foreach ($function in $ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] }, $false)) {
    if ($function.Name -in @('Assert-Condition', 'Read-OneJson')) { Invoke-Expression $function.Extent.Text }
}
$compact = Read-OneJson '{"completed":true,"factsStatus":"Healthy"}' 'Compact'
Assert-Condition ($compact.completed -eq $true -and $compact.factsStatus -ceq 'Healthy') 'Compact JSON regression.'
$pretty = Read-OneJson (@('{', '  "completed": true,', '  "factsStatus": "Healthy"', '}') -join [Environment]::NewLine) 'Pretty'
Assert-Condition ($pretty.completed -eq $true -and $pretty.factsStatus -ceq 'Healthy') 'Multiline JSON regression.'
foreach ($invalid in @('', '  ', 'noise {"ok":true}', '{"ok":true} noise', '{"ok":true}{"other":1}', ('{"ok":true}' + [Environment]::NewLine + '{"other":1}'), '[]', 'null')) {
    $rejected = $false
    try { [void](Read-OneJson $invalid 'Invalid') }
    catch { $rejected = $true }
    Assert-Condition $rejected 'Empty, extra-content, multiple-object or non-object stdout was accepted.'
}
Write-Host 'Read-OneJson pure helper regressions passed (compact, multiline, eight rejected inputs); no child process/dotnet invoked.'
