<#
.SYNOPSIS
    Tests the caption merge in src\CaptionLog.cs.

.DESCRIPTION
    Feeds simulated Teams caption screens into the merge and checks the transcript comes
    out exactly as spoken. The simulation covers lines growing word by word, the last word
    being corrected, two people talking at once, rows scrolling off the top, earlier lines
    touched up, a line folded into the one before it, and polls that miss screens.

    With -RawLog, also replays a file written by Save-TeamsCaptions.ps1 -RawLog and checks
    that after every screen the end of the transcript matches what was on screen.

    Run it in a fresh PowerShell, since a session keeps the first version of the C# it loads.

.EXAMPLE
    powershell -NoProfile -File .\Test-CaptionLog.ps1 -RawLog .\raw.jsonl
#>
param(
    [int]$Runs = 25,
    [int]$Lines = 250,
    [string]$RawLog,
    [int]$Seed,         # run only this seed
    [int]$TraceFrom     # with -Seed: print screens and transcript from this merge on
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

# The same C# the script and the app use.
Add-Type -Path (Join-Path $PSScriptRoot 'src\CaptionLog.cs')

$Speakers = 'Alex Rivera', 'Sam Lee', 'Jordan Park'
$Short = 'Yeah.', 'OK.', 'Mm-hmm.', 'Right.', 'Yes.'
$Vocab = ('so we need to look at the plan for next week and the draft the budget the schedule and ' +
    'the notes this that is a good point but I think it would be fine to move it to Friday').Split(' ')

# Small checks of the helpers the watcher relies on.
$checks = @(
    @{ Got = [TeamsCaptions.TranscriptFile]::MeetingTitle('Weekly sync | Microsoft Teams'); Want = 'Weekly sync' },
    @{ Got = [TeamsCaptions.TranscriptFile]::MeetingTitle('Weekly sync | Microsoft Teams | Pinned window'); Want = 'Weekly sync' },
    @{ Got = [TeamsCaptions.TranscriptFile]::MeetingTitle(''); Want = 'Teams meeting' }
)
$probe = New-Object TeamsCaptions.CaptionLog
[void]$probe.Merge([string[]]@('Sam Lee', 'Alex Rivera', 'Sam Lee'), [string[]]@('The draft is ready for review today.', 'OK.', 'Can you send it over?'), [datetime]'2026-01-01T10:00:00')
$checks += @{ Got = $probe.Overlap([string[]]@('Alex Rivera', 'Sam Lee'), [string[]]@('OK.', 'Can you send it over?')); Want = 2 }
$checks += @{ Got = $probe.Overlap([string[]]@('Jordan Park'), [string[]]@('Something else entirely.')); Want = 0 }
$unitFails = 0
foreach ($c in $checks) {
    if ($c.Got -cne $c.Want) { $unitFails++; Write-Host "FAIL check: expected '$($c.Want)', got '$($c.Got)'" }
}
Write-Host ('Checks: {0} of {1} pass' -f ($checks.Count - $unitFails), $checks.Count)

function New-TrueLines($Rng, [int]$Count) {
    for ($i = 0; $i -lt $Count; $i++) {
        $speaker = $Speakers[$Rng.Next($Speakers.Count)]
        if ($Rng.NextDouble() -lt 0.3) {
            $final = $Short[$Rng.Next($Short.Count)]
            $words = @($final.TrimEnd('.'))
        }
        else {
            $words = @(for ($w = $Rng.Next(2, 14); $w -gt 0; $w--) { $Vocab[$Rng.Next($Vocab.Count)] })
            $words[0] = $words[0].Substring(0, 1).ToUpper() + $words[0].Substring(1)
            $final = ($words -join ' ') + '.'
        }
        [pscustomobject]@{ Speaker = $speaker; Words = $words; Final = $final }
    }
}

function Compare-Tail($Log, [string[]]$Sp, [string[]]$Tx) {
    $e = $Log.Entries
    $m = $Sp.Count
    if ($e.Count -lt $m) { return "log has $($e.Count) lines but the screen shows $m" }
    for ($i = 0; $i -lt $m; $i++) {
        $x = $e[$e.Count - $m + $i]
        if ($x.Speaker -cne $Sp[$i] -or $x.Text -cne $Tx[$i]) {
            return "screen row $i of ${m}: expected [$($Sp[$i])] '$($Tx[$i])', log has [$($x.Speaker)] '$($x.Text)'"
        }
    }
    $null
}

function Invoke-Simulation([int]$Seed) {
    $rng = New-Object System.Random($Seed)
    $plan = @(New-TrueLines $rng $Lines)
    $log = New-Object TeamsCaptions.CaptionLog
    $truth = New-Object System.Collections.Generic.List[object]
    $screen = New-Object System.Collections.Generic.List[object]
    $active = New-Object System.Collections.Generic.List[object]
    $next = 0
    $t = [datetime]'2026-11-11T15:00:00'
    $skip = 0
    $merges = 0
    while ($next -lt $plan.Count -or $active.Count) {
        # A second person may start while someone is still talking (never the same person).
        $overlap = $next -lt $plan.Count -and $active.Count -eq 1 -and $active[0].Speaker -ne $plan[$next].Speaker -and $rng.NextDouble() -lt 0.15
        if ($next -lt $plan.Count -and ($active.Count -eq 0 -or $overlap)) {
            $p = $plan[$next]; $next++
            $row = [pscustomobject]@{ Speaker = $p.Speaker; Words = $p.Words; Final = $p.Final; Shown = 0; Text = ''; Done = $false }
            if ($overlap -and $rng.NextDouble() -lt 0.5) {
                # Teams sometimes slots the newcomer in above the line still being spoken.
                $screen.Insert($screen.IndexOf($active[0]), $row)
                $truth.Insert($truth.IndexOf($active[0]), $row)
            }
            else {
                $screen.Add($row); $truth.Add($row)
            }
            $active.Add($row)
        }
        $row = $active[$rng.Next($active.Count)]
        $row.Shown++
        if ($row.Shown -ge $row.Words.Count) {
            $row.Text = $row.Final
            $row.Done = $true
            [void]$active.Remove($row)
            $idx = $screen.IndexOf($row)
            $roll = $rng.NextDouble()
            if ($roll -lt 0.03 -and $idx -gt 0 -and $screen[$idx - 1].Done -and $screen[$idx - 1].Speaker -eq $row.Speaker) {
                # Teams folds this line into the previous line from the same speaker.
                $screen[$idx - 1].Text = $screen[$idx - 1].Text + ' ' + $row.Text
                $screen.RemoveAt($idx)
                [void]$truth.Remove($row)
            }
            elseif ($roll -lt 0.10) {
                # Teams touches up a recent finished line: punctuation, or one word.
                $done = @($screen | Where-Object { $_.Done -and $_ -ne $row })
                if ($done.Count) {
                    $d = $done[$done.Count - 1 - $rng.Next([Math]::Min(3, $done.Count))]
                    if ($rng.NextDouble() -lt 0.5) { $d.Text = $d.Text.TrimEnd('.', '?') + '?' }
                    else {
                        $w = $d.Text.Split(' ')
                        if ($w.Count -ge 4) { $w[$w.Count - 2] = 'actually'; $d.Text = $w -join ' ' }
                    }
                }
            }
        }
        else {
            $shown = @($row.Words[0..($row.Shown - 1)])
            if ($rng.NextDouble() -lt 0.15) { $shown[$shown.Count - 1] = 'uh' }   # last word misheard, fixed next time
            $row.Text = $shown -join ' '
        }
        while ($screen.Count -gt 40) { $screen.RemoveAt(0) }
        $t = $t.AddMilliseconds(700)

        if ($skip -gt 0) { $skip--; continue }
        if ($rng.NextDouble() -lt 0.02) { $skip = 12; continue }   # a run of missed polls
        if ($rng.NextDouble() -lt 0.3) { continue }                # one missed poll
        $visible = @($screen | Where-Object { $_.Text })
        $sp = [string[]]@($visible | ForEach-Object { $_.Speaker })
        $tx = [string[]]@($visible | ForEach-Object { $_.Text })
        if ($TraceFrom -and $merges + 1 -ge $TraceFrom - 2) {
            Write-Host "--- before merge $($merges + 1): log tail"
            $e = $log.Entries
            for ($i = [Math]::Max(0, $e.Count - 6); $i -lt $e.Count; $i++) { Write-Host ('  [{0}] {1}' -f $e[$i].Speaker, $e[$i].Text) }
            Write-Host '    screen bottom:'
            for ($i = [Math]::Max(0, $sp.Count - 6); $i -lt $sp.Count; $i++) { Write-Host ('  [{0}] {1}' -f $sp[$i], $tx[$i]) }
        }
        [void]$log.Merge($sp, $tx, $t)
        $merges++
        $bad = Compare-Tail $log $sp $tx
        if ($bad) { return "seed ${Seed}: after merge ${merges}: $bad" }
    }
    $visible = @($screen | Where-Object { $_.Text })
    [void]$log.Merge([string[]]@($visible | ForEach-Object { $_.Speaker }), [string[]]@($visible | ForEach-Object { $_.Text }), $t.AddSeconds(1))

    $e = $log.Entries
    for ($i = 0; $i -lt [Math]::Max($e.Count, $truth.Count); $i++) {
        $want = if ($i -lt $truth.Count) { '[{0}] {1}' -f $truth[$i].Speaker, $truth[$i].Text } else { '(nothing)' }
        $got = if ($i -lt $e.Count) { '[{0}] {1}' -f $e[$i].Speaker, $e[$i].Text } else { '(nothing)' }
        if ($want -cne $got) { return "seed ${Seed}: line ${i} of $($truth.Count): expected '$want', got '$got'" }
    }
    for ($i = 1; $i -lt $e.Count; $i++) {
        if ($e[$i].FirstSeen -lt $e[$i - 1].FirstSeen) { return "seed ${Seed}: line $i is timed $($e[$i].FirstSeen.ToString('HH:mm:ss.f')), before the line above it ($($e[$i - 1].FirstSeen.ToString('HH:mm:ss.f')))" }
    }
    $null
}

$failed = $unitFails
$seeds = if ($Seed) { @($Seed) } else { 1..$Runs }
foreach ($s in $seeds) {
    $result = Invoke-Simulation $s
    if ($result) { $failed++; Write-Host "FAIL $result" }
}
Write-Host ('Simulation: {0} of {1} runs exact, times in order ({2} lines each)' -f ($seeds.Count - $failed + $unitFails), $seeds.Count, $Lines)

if ($RawLog) {
    $log = New-Object TeamsCaptions.CaptionLog
    $n = 0
    $tailFails = 0
    foreach ($line in [System.IO.File]::ReadAllLines($RawLog)) {
        if (-not $line.Trim()) { continue }
        $snap = $line | ConvertFrom-Json
        if ($snap.PSObject.Properties['event']) { $log.Break(); continue }
        $sp = [string[]]@($snap.speakers)
        $tx = [string[]]@($snap.texts)
        [void]$log.Merge($sp, $tx, [datetime]$snap.t)
        $n++
        $bad = Compare-Tail $log $sp $tx
        if ($bad) {
            $tailFails++
            if ($tailFails -le 5) { Write-Host "TAIL screen ${n}: $bad" }
        }
    }
    Write-Host ('Replay: {0} screens, {1} transcript lines, {2} screens where the transcript end differed from the screen' -f $n, $log.Count, $tailFails)
    if ($tailFails) { $failed++ }
}

if ($failed) { exit 1 }
