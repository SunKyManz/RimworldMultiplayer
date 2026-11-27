<#
.SYNOPSIS
    Checks that the Multiplayer mod simulates a replay identically when it is run more than once.

.DESCRIPTION
    Runs RimWorld headless several times on the same multiplayer replay (any save in the MpReplays folder),
    using the mod's -replay command-line mode. Each run replays the recorded commands, keeps playing for
    -Ticks more ticks, and writes a fingerprint line every -Interval ticks: the world and map random states
    plus a hash of every map's things. The script then compares the runs and reports the first tick
    where they differ. Identical runs mean no desync from this replay on this PC.

    Each run gets its own save data folder (-savedatafolder) with a minimal mod list (-ModIds), so your real
    mod list, settings and saves are never touched. The replay is copied in from your MpReplays folder.
    The mods must be installed in the game's Mods folder or from the Workshop, including this repo's build
    (e.g. a Mods\Multiplayer junction to the repo).

    RimWorld 1.7+ (no longer Unity) has no headless mode, so every run opens a game window there and
    -WithGraphics has no effect.

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
    ./Tools/Check-Determinism.ps1 -Replay Autosave-1 -UiNoise
    Runs every pass after the first while imitating a busy player: between ticks it selects random things and
    runs inspect strings, gizmos, float menus, tooltips, stat explanations and alerts. That code must not change
    the game, so any difference from the first (quiet) run is interface code causing a desync.

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

    # Run every pass after the first with simulated interface activity (see the -UiNoise example).
    [Parameter(ParameterSetName = 'Run')]
    [switch]$UiNoise,

    # Ticks between interface noise passes. Real play runs the interface every tick or two; smaller is
    # closer to that but slower.
    [Parameter(ParameterSetName = 'Run')]
    [ValidateRange(1, [int]::MaxValue)]
    [int]$NoiseEvery = 10,

    # RimWorld.exe (1.7+) or RimWorldWin64.exe (Unity builds up to 1.6). Defaults to whichever is in the Steam folder.
    [Parameter(ParameterSetName = 'Run')]
    [string]$RimWorld,

    # Mods active during the runs, in load order. Must match the mods the replay was recorded with.
    [Parameter(ParameterSetName = 'Run')]
    [string[]]$ModIds = @(
        'brrainz.harmony', 'ludeon.rimworld', 'ludeon.rimworld.royalty', 'ludeon.rimworld.ideology',
        'ludeon.rimworld.biotech', 'ludeon.rimworld.anomaly', 'ludeon.rimworld.odyssey', 'rwmt.multiplayer'),

    # Ticks (as printed in a DIVERGED report) at which every run also writes all things to
    # runN.things-<tick>.txt, so you can diff them to see which thing differs.
    [Parameter(ParameterSetName = 'Run')]
    [int[]]$DumpTicks,

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
$userDataDir = Join-Path $env:USERPROFILE 'AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios'
$replaysDir = Join-Path $userDataDir 'MpReplays'

# Fills a run's save data folder: the replay, the -ModIds mod list and copies of your general preferences
function New-RunSaveData([string]$dir) {
    $config = New-Item -ItemType Directory -Force -Path (Join-Path $dir 'Config')
    $replays = New-Item -ItemType Directory -Force -Path (Join-Path $dir 'MpReplays')
    Copy-Item -LiteralPath (Join-Path $replaysDir "$Replay.zip") -Destination $replays

    # Prefs keep first-launch dialogs (language and so on) from blocking the run
    foreach ($file in 'Prefs.xml', 'KeyPrefs.xml', 'Knowledge.xml') {
        $source = Join-Path $userDataDir "Config\$file"
        if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $config }
    }

    $versionFile = Join-Path (Split-Path $RimWorld) 'Version.txt'
    $version = if (Test-Path -LiteralPath $versionFile) { (Get-Content -LiteralPath $versionFile -TotalCount 1).Trim() } else { '' }
    $toItems = { param($ids) ($ids | ForEach-Object { "    <li>$_</li>" }) -join "`n" }
    $xml = @(
        '<?xml version="1.0" encoding="utf-8"?>'
        '<ModsConfigData>'
        "  <version>$version</version>"
        '  <activeMods>'
        (& $toItems $ModIds)
        '  </activeMods>'
        '  <knownExpansions>'
        (& $toItems ($ModIds | Where-Object { $_ -like 'ludeon.rimworld.*' }))
        '  </knownExpansions>'
        '</ModsConfigData>'
    ) -join "`n"
    Set-Content -LiteralPath (Join-Path $config 'ModsConfig.xml') -Value $xml -Encoding utf8
}

# The log the game wrote during a run that started at $since, or $null
function Find-GameLog([string]$saveData, [datetime]$since) {
    @(
        (Join-Path $saveData 'Player.log'), (Join-Path $saveData 'Logs\Player.log'),
        (Join-Path $userDataDir 'Player.log'), (Join-Path $userDataDir 'Logs\Player.log')
    ) | Where-Object { (Test-Path -LiteralPath $_) -and (Get-Item -LiteralPath $_).LastWriteTime -ge $since } |
        Select-Object -First 1
}

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
        Write-Host "  To see which thing differs: rerun with -DumpTicks $($fa['tick']) and diff the runN.things-$($fa['tick']).txt files."
        Write-Host '  Random state differs = a different number of random calls. Things hash only = an item, pawn or job changed without (yet) using random numbers differently.'
        if ($UiNoise) { Write-Host '  With -UiNoise: interface code changed the game. Check the noisy run''s log for UI noise warnings.' }
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
if (-not $RimWorld) {
    $steamDir = Join-Path ${env:ProgramFiles(x86)} 'Steam\steamapps\common\RimWorld'
    $RimWorld = @('RimWorld.exe', 'RimWorldWin64.exe') | ForEach-Object { Join-Path $steamDir $_ } |
        Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not $RimWorld -or -not (Test-Path -LiteralPath $RimWorld)) { Stop-Check "RimWorld not found. Pass -RimWorld <path to RimWorld.exe or RimWorldWin64.exe>." }
$RimWorld = (Resolve-Path -LiteralPath $RimWorld).Path

# Unity builds (up to 1.6) can run headless and take -logfile; the 1.7 engine can't
$unity = Test-Path -LiteralPath (Join-Path (Split-Path $RimWorld) 'UnityPlayer.dll')
if (-not $unity -and $WithGraphics) { Write-Host '-WithGraphics has no effect: this RimWorld build always runs with graphics.' -ForegroundColor Yellow }

$OutDir = (New-Item -ItemType Directory -Force -Path $OutDir).FullName
Write-Host "Replay '$Replay': $Runs runs of $Ticks ticks, fingerprint every $Interval ticks. Output: $OutDir"
Write-Host "Mods: $($ModIds -join ', ')"

$fingerprintFiles = for ($run = 1; $run -le $Runs; $run++) {
    $name = "run$run"
    $graphics = $WithGraphics -and $run -gt 1
    $noise = $UiNoise -and $run -gt 1
    $log = Join-Path $OutDir "$name.log"
    # A fresh save data folder per run, so nothing one run writes (settings, caches) can affect the next
    $saveData = Join-Path $OutDir "$name-savedata"
    New-RunSaveData $saveData
    $arguments = @(
        if ($unity -and -not $graphics) { '-batchmode'; '-nographics' }
        if ($unity) { '-logfile'; "`"$log`"" }
        "`"-savedatafolder=$saveData`""
        "`"-replay=${name}:${Replay}:${Ticks}:${Interval}`""
        "`"-replaydata=$OutDir`""
        # A different seed per run, so each noisy run pokes different things
        if ($noise) { "-replaynoise=${run}:${NoiseEvery}" }
        if ($DumpTicks) { "-replaydump=$($DumpTicks -join ',')" }
    ) -join ' '

    $modes = @(if ($graphics) { 'with graphics' }; if ($noise) { "with UI noise every $NoiseEvery ticks" }) -join ', '
    Write-Host "Run $run/$Runs$(if ($modes) { " ($modes)" })..."
    $started = Get-Date
    $process = Start-Process -FilePath $RimWorld -ArgumentList $arguments -WorkingDirectory (Split-Path $RimWorld) -PassThru
    $finished = $process.WaitForExit($TimeoutMinutes * 60 * 1000)
    if (-not $finished) { $process.Kill(); $process.WaitForExit() }
    if (-not $unity) {
        $gameLog = Find-GameLog $saveData $started
        if ($gameLog) { Copy-Item -LiteralPath $gameLog -Destination $log }
    }
    if (-not $finished) { Stop-Check "Run $run took longer than $TimeoutMinutes minutes and was stopped. Log: $log" }

    $file = Join-Path $OutDir "$name.ticks"
    if (-not (Test-Path -LiteralPath $file)) { Stop-Check "Run $run wrote no fingerprints; the replay probably failed to load. Log: $log" }
    if (-not (Select-String -LiteralPath $file -Pattern ' map\d+=' -Quiet)) {
        Write-Host "  Warning: no map loaded in run $run, so only the world is checked. The replay's mods are probably missing (see -ModIds). Log: $log" -ForegroundColor Yellow
    }

    if ($noise) {
        $noiseFile = Join-Path $OutDir "$name.noise"
        if (-not (Test-Path -LiteralPath $noiseFile)) { Stop-Check "Run $run wrote no UI noise summary; this build of the mod may not support -replaynoise. Log: $log" }
        $summary = @(Get-Content -LiteralPath $noiseFile)
        Write-Host "  UI noise: $($summary[0])"
        if ($summary[0] -match '(^| )(passes|calls)=0( |$)') { Stop-Check "Run $run's UI noise never ran or found nothing to poke (no maps loaded?), so it tested nothing. Log: $log" }
        if ($summary.Count -gt 1) { Write-Host '  (Calls that threw are expected headless: no textures or GUI. They still ran up to the throw.)' }
        $summary | Select-Object -Skip 1 | ForEach-Object { Write-Host "    $_" }
    }
    $file
}

$identical = $true
for ($run = 2; $run -le $Runs; $run++) {
    Write-Host "Comparing run 1 with run ${run}:"
    if (-not (Compare-Fingerprints $fingerprintFiles[0] $fingerprintFiles[$run - 1])) { $identical = $false }
}
exit $(if ($identical) { 0 } else { 1 })
