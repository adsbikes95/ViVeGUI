# Read-only smoke test for the ViVeTool CLI on a real Windows machine (used by CI).
# Only commands that do not modify feature configuration are executed.
param([Parameter(Mandatory = $true)][string]$ViVeTool)
$ErrorActionPreference = 'Stop'
$failures = 0

function Invoke-Case([string]$Name, [string[]]$Arguments, [int]$ExpectedExitCode, [string]$ExpectedText) {
    $output = & $ViVeTool @Arguments 2>&1 | Out-String
    $code = $LASTEXITCODE
    $ok = ($code -eq $ExpectedExitCode) -and ([string]::IsNullOrEmpty($ExpectedText) -or $output.Contains($ExpectedText))
    if ($ok) { Write-Host "PASS  $Name (exit $code)" }
    else {
        Write-Host "FAIL  $Name (exit $code, expected $ExpectedExitCode)"
        Write-Host $output
        $script:failures++
    }
}

Invoke-Case 'no arguments prints help'        @()                         0 'ViVeTool'
Invoke-Case '/? prints help'                  @('/?')                     0 '/enable'
Invoke-Case '/changestamp'                    @('/changestamp')           0 ''
Invoke-Case '/query (all, runtime store)'     @('/query')                 0 ''
Invoke-Case 'unknown command'                 @('/definitelynotacommand') 2 ''
Invoke-Case 'legacy command migration tip'    @('queryconfig')            2 '/query'
Invoke-Case '/enable without IDs (no change)' @('/enable')                2 ''
Invoke-Case '/query invalid store'            @('/query', '/store:both')  2 ''
# Feature ID 4294967295 is not expected to have a configuration; querying it is read-only.
Invoke-Case '/query unconfigured ID'          @('/query', '/id:4294967295') 4 'No configuration'

if ($failures -gt 0) {
    Write-Error "$failures CLI smoke test case(s) failed."
    exit 1
}
# The last case deliberately leaves $LASTEXITCODE at 4; exit explicitly so the
# runner does not report that expected code as a step failure.
Write-Host 'All CLI smoke test cases passed.'
exit 0
