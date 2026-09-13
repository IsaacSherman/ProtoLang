<#
.SYNOPSIS
    Says what the run did not do, and fails when what it did not do was the point.

.DESCRIPTION
    A test run reports failures loudly and skips silently, and the two are not equally visible for
    equally good reasons. A skip is usually correct: this suite declines a C++ vector where there is
    no C++ toolchain, and declines a well-known-schema check under a protoc that ships those schemas
    as compiled-in descriptors rather than as files. Those are honest answers about the machine.

    The dishonest case is a gate that did not open. The corpus sweep is switched on by an environment
    variable, and a workflow that sets it in the wrong scope, or a rename that leaves the variable
    behind, produces a green build in which the most thorough check in the repository never ran.
    Nothing about that is visible in an exit code -- the run passes, faster than usual, and the
    absence looks exactly like success. So the gated tests are named here and their absence is a
    failure, which is the one thing that makes switching them on in CI mean anything.

    Everything else that was skipped is reported and not judged, so that a reviewer reading the job
    summary can see the shape of what ran on this machine.
#>
[CmdletBinding()]
param(
    # Where 'dotnet test --results-directory' put the .trx.
    [Parameter(Mandatory)]
    [string] $Results,

    # Tests that exist to be run here, by substring of their fully qualified name. A run that skips
    # one of these is a run whose gate did not open.
    #
    # PROTOLANG_BENCH is deliberately absent. It measures wall-clock latency, and a deadline on a
    # shared runner flakes until somebody loosens it past the point of describing anything. What CI
    # checks of the performance work is PerformanceCostTests, which counts work rather than time and
    # runs unconditionally. See docs/performance.md.
    [string[]] $Required = @(
        'EveryItemOfferedAnywhereInTheCorpusBindsWhenItIsAccepted',
        'ALongSessionOfEditingLeavesNoBacklogProcessesOrTemporaryFiles')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$trx = Get-ChildItem -Path $Results -Filter *.trx -Recurse -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime |
    Select-Object -Last 1

if (-not $trx) {
    throw "No .trx under '$Results'. The test step did not produce a report, so nothing here can be checked."
}

[xml] $document = Get-Content -LiteralPath $trx.FullName -Raw
$namespace = @{ t = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010' }

$outcomes = Select-Xml -Xml $document -XPath '//t:UnitTestResult' -Namespace $namespace |
    ForEach-Object { $_.Node }

$skipped = @($outcomes | Where-Object { $_.outcome -eq 'NotExecuted' })
$failed = @($outcomes | Where-Object { $_.outcome -eq 'Failed' })
$passed = @($outcomes | Where-Object { $_.outcome -eq 'Passed' })

$lines = @(
    '## Test run',
    '',
    "$($passed.Count) passed, $($failed.Count) failed, $($skipped.Count) skipped.",
    ''
)

if ($skipped.Count -gt 0) {
    $lines += '<details><summary>Skipped</summary>', ''
    $lines += $skipped | ForEach-Object { "- ``$($_.testName)``" }
    $lines += '', '</details>', ''
}

# Named one at a time rather than counted, so the message says which gate stayed shut.
$shut = @()

foreach ($name in $Required) {
    $matching = @($outcomes | Where-Object { $_.testName -like "*$name*" })
    $ran = @($matching | Where-Object { $_.outcome -ne 'NotExecuted' })

    if ($matching.Count -eq 0) {
        $shut += "'$name' matched no test at all. It was renamed or removed, and this check has been " +
            'guarding nothing since.'
        continue
    }

    if ($ran.Count -eq 0) {
        $shut += "'$name' was skipped in all $($matching.Count) of its cases. Its gate did not open."
    }
}

if ($shut.Count -gt 0) {
    $lines += '### Gates that did not open', ''
    $lines += $shut | ForEach-Object { "- $_" }
    $lines += ''
}

if ($env:GITHUB_STEP_SUMMARY) {
    $lines | Out-File -FilePath $env:GITHUB_STEP_SUMMARY -Append -Encoding utf8
}

$lines | ForEach-Object { Write-Host $_ }

if ($shut.Count -gt 0) {
    throw 'A gated test did not run. See "Gates that did not open" above.'
}
