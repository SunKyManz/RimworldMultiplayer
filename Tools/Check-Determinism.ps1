<#
.SYNOPSIS
    Checks that the Multiplayer mod simulates a replay identically when it is run more than once.

.DESCRIPTION
    Runs RimWorld headless several times on the same multiplayer replay (any save in the MpReplays folder),
    using the mod's -replay command-line mode. Each run replays the recorded commands, keeps playing for
    -Ticks more ticks, and writes a fingerprint line every -Interval ticks: the world and map random states
    plus a hash of every map's things. The script then compares the runs and reports the first tick
    where they differ. Identical runs mean no desync from this replay on this PC.

    To check for differences between PCs (e.g. CPU float differences), run this on each PC with the
    same replay and mods, then compare the .ticks files with -Compare.

.EXAMPLE
    ./Tools/Check-Determinism.ps1
    Lists the replays you can test.

.EXAMPLE
    ./Tools/Check-Determinism.ps1 -Replay Autosave-1

.EXAMPLE
    ./Tools/Check-Determinism.ps1 -Replay Autosave-1 -WithGraphics
    Runs the second pass with graphics on, which catches desyncs caused by code that only runs when drawing.

.EXAMPLE
    ./Tools/Check-Determinism.ps1 -Compare mine.ticks -Against friend.ticks
#>
[CmdletBinding(DefaultParameterSetName = 'Run')]
param(
    [Parameter(ParameterSetName = 'Run', Position = 0)]
    [string]$Replay,

    # Ticks to keep simulating after the recorded commands. 60000 ticks = 1 in-game day.
    [Parameter(ParameterSetName = 'Run')]
    [ValidateRange(1, [int]::MaxValue)]
    [int]$Ticks = 60000,

    # Ticks between fingerprints. Smaller pinpoints the divergence more precisely but runs a little slower.
    [Parameter(ParameterSetName = 'Run')]
    [ValidateRange(1, [int]::MaxValue)]
    [int]$Interval = 250,

    [Parameter(ParameterSetName = 'Run')]
    [ValidateRange(2, 10)]
    [int]$Runs = 2,

    # Run every pass after the first with graphics (needs a desktop session; slower).
    [Parameter(ParameterSetName = 'Run')]
    [switch]$WithGraphics,

    [Parameter(ParameterSetName = 'Run')]
    [string]$RimWorld = (Join-Path ${env:ProgramFiles(x86)} 'Steam\steamapps\common\RimWorld\RimWorldWin64.exe'),

    [Parameter(ParameterSetName = 'Run')]
    [string]$OutDir = (Join-Path $PSScriptRoot "..\determinism-runs\$(Get-Date -Format 'yyyyMMdd-HHmmss')"),

    [Parameter(ParameterSetName = 'Run')]
    [int]$TimeoutMinutes = 60,

    # Compare two existing .ticks files, e.g. one from your PC and one from a friend's.
    [Parameter(ParameterSetName = 'Compare', Mandatory)]
    [string]$Compare,

    [Parameter(ParameterSetName = 'Compare', Mandatory)]
    [string]$Against
)

$ErrorActionPreference = 'Stop'

# Exit codes: 0 = identical, 1 = diverged, 2 = the check itself could not run.
function Stop-Check([string]$message) {
    Write-Host $message -ForegroundColor Red
    exit 2
}
$replaysDir = Join-Path $env:USERPROFILE 'AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\MpReplays'

# Splits "tick=1 world=5/9 map0=5/9/ABCD" into an ordered key -> value map.
function ConvertFrom-Fingerprint([string]$line) {
    $fields = [ordered]@{}
    foreach ($token in $line -split ' ') {
        $key, $value = $token -split '=', 2
        $fields[$key] = $value
    }
    $fields
}

function Get-FieldDifference([string]$key, [string]$a, [string]$b) {
    if ($null -eq $a) { return "  $key only exists in the second run" }
    if ($null -eq $b) { return "  $key only exists in the first run" }

    $names = if ($key -eq 'world') { 'ticks', 'random state' } else { 'ticks', 'random state', 'things hash' }
    $partsA = $a -split '/'
    $partsB = $b -split '/'
    $diffs = for ($i = 0; $i -lt $names.Count; $i++) {
        if ($partsA[$i] -ne $partsB[$i]) { "$($names[$i]) $($partsA[$i]) vs $($partsB[$i])" }
    }
    "  ${key}: " + ($diffs -join ', ')
}

# Returns $true when both fingerprint files match, printing the first difference otherwise.
function Compare-Fingerprints([string]$fileA, [string]$fileB) {
    $a = @(Get-Content -LiteralPath $fileA)
    $b = @(Get-Content -LiteralPath $fileB)
    $lastMatch = $null

    for ($i = 0; $i -lt [Math]::Min($a.Count, $b.Count); $i++) {
        if ($a[$i] -eq $b[$i]) { $lastMatch = $a[$i]; continue }

        $fa = ConvertFrom-Fingerprint $a[$i]
        $fb = ConvertFrom-Fingerprint $b[$i]
        Write-Host "DIVERGED at sample $($i + 1) (tick $($fa['tick']))." -ForegroundColor Red
        if ($lastMatch) { Write-Host "  Last matching sample: tick $((ConvertFrom-Fingerprint $lastMatch)['tick'])" }
        else { Write-Host '  The very first sample differs: the replay commands already played out differently.' }

        foreach ($key in @($fa.Keys) + @($fb.Keys) | Select-Object -Unique) {
            if ($key -ne 'tick' -and $fa[$key] -ne $fb[$key]) { Write-Host (Get-FieldDifference $key $fa[$key] $fb[$key]) }
        }
        Write-Host '  Random state differs = a different number of random calls. Things hash only = an item, pawn or job changed without (yet) using random numbers differently.'
        return $false
    }

    if ($a.Count -ne $b.Count) {
        Write-Host "Runs have a different number of samples ($($a.Count) vs $($b.Count)); one probably crashed or timed out." -ForegroundColor Yellow
        return $false
    }

    Write-Host "Identical: $($a.Count) samples match." -ForegroundColor Green
    $true
}

if ($PSCmdlet.ParameterSetName -eq 'Compare') {
    foreach ($file in $Compare, $Against) { if (-not (Test-Path -LiteralPath $file)) { Stop-Check "File not found: $file" } }
    exit $(if (Compare-Fingerprints $Compare $Against) { 0 } else { 1 })
}

if (-not $Replay) {
    Write-Host "Replays in ${replaysDir}:"
    Get-ChildItem -LiteralPath $replaysDir -Filter *.zip | Sort-Object LastWriteTime -Descending |
        ForEach-Object { '  {0}  ({1:yyyy-MM-dd HH:mm})' -f $_.BaseName, $_.LastWriteTime }
    Write-Host 'Run again with -Replay <name>.'
    exit 0
}

$Replay = $Replay -replace '\.zip$', ''
if ($Replay -match '[:;]') { Stop-Check "Replay names with ':' or ';' aren't supported by the mod's -replay option. Rename the file." }
if (-not (Test-Path -LiteralPath (Join-Path $replaysDir "$Replay.zip"))) { Stop-Check "Replay '$Replay' not found in $replaysDir. Run without -Replay to list them." }
if (-not (Test-Path -LiteralPath $RimWorld)) { Stop-Check "RimWorld not found at $RimWorld. Pass -RimWorld <path to RimWorldWin64.exe>." }

$OutDir = (New-Item -ItemType Directory -Force -Path $OutDir).FullName
Write-Host "Replay '$Replay': $Runs runs of $Ticks ticks, fingerprint every $Interval ticks. Output: $OutDir"

$fingerprintFiles = for ($run = 1; $run -le $Runs; $run++) {
    $name = "run$run"
    $graphics = $WithGraphics -and $run -gt 1
    $arguments = @(
        if (-not $graphics) { '-batchmode'; '-nographics' }
        '-logfile'; "`"$(Join-Path $OutDir "$name.log")`""
        "`"-replay=${name}:${Replay}:${Ticks}:${Interval}`""
        "`"-replaydata=$OutDir`""
    ) -join ' '

    Write-Host "Run $run/$Runs$(if ($graphics) { ' (with graphics)' })..."
    $process = Start-Process -FilePath $RimWorld -ArgumentList $arguments -PassThru
    if (-not $process.WaitForExit($TimeoutMinutes * 60 * 1000)) {
        $process.Kill()
        Stop-Check "Run $run took longer than $TimeoutMinutes minutes and was stopped. Log: $(Join-Path $OutDir "$name.log")"
    }

    $file = Join-Path $OutDir "$name.ticks"
    if (-not (Test-Path -LiteralPath $file)) { Stop-Check "Run $run wrote no fingerprints; the replay probably failed to load. Log: $(Join-Path $OutDir "$name.log")" }
    $file
}

$identical = $true
for ($run = 2; $run -le $Runs; $run++) {
    Write-Host "Comparing run 1 with run ${run}:"
    if (-not (Compare-Fingerprints $fingerprintFiles[0] $fingerprintFiles[$run - 1])) { $identical = $false }
}
exit $(if ($identical) { 0 } else { 1 })
