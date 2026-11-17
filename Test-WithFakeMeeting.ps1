<#
.SYNOPSIS
    End-to-end test: runs Save-TeamsCaptions.ps1 (or the app) against fake captions windows in Edge.

.DESCRIPTION
    Writes a page shaped like the Teams live captions pop-out (the same classes and ids),
    opens it in Edge with a throwaway profile, and runs the saver against it. The page
    "speaks" scripted lines word by word, mishears some words and fixes them, starts each
    row with a name and no words, and keeps only the last few rows, like Teams.

    Meeting A is "pinned" halfway through, the way Teams does it: its window is replaced by
    one titled "... | Microsoft Teams | Pinned window" that shows the recent lines and
    carries on. The saver must keep writing the same transcript.

    The test then closes the window and checks that the saver noticed, closed the
    transcript, and wrote every line once, in order, in one file per meeting.

    With -Watch it plays a second meeting (B) afterwards and expects a second transcript.

    It opens a small browser window for about 40 seconds per meeting.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\Test-WithFakeMeeting.ps1 -Watch
#>
param(
    [int]$Lines = 40,
    [int]$WordMs = 120,
    [int]$Cap = 15,
    [switch]$Watch,
    [switch]$Keep,          # keep the temporary folder (pages, profile, transcripts, saver output)
    [switch]$SaverVerbose,  # run the script with -Verbose (handled errors and slow reads)
    [switch]$Pwsh,          # run the script in PowerShell 7 instead of Windows PowerShell 5.1
    [string]$App,           # test this TeamsCaptions.exe instead of the script (always two meetings)
    [switch]$ShowApp,       # with -App: show its window instead of starting it in the notification area
    [string]$OutDir,        # where the transcripts go (default: inside the temporary folder)
    [int]$CloseWaitSeconds = 30
)
if ($App) { $Watch = [switch]$true }

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -Namespace E2E -Name Win -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
[DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
'@

$saver = Join-Path $PSScriptRoot 'Save-TeamsCaptions.ps1'
$saverHost = if ($Pwsh) { (Get-Command pwsh -ErrorAction Stop).Source } else { Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe' }
$edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") |
    Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $edge) { throw 'This test needs Microsoft Edge.' }

$work = Join-Path ([System.IO.Path]::GetTempPath()) ('teams-captions-e2e-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$out = if ($OutDir) { $OutDir } else { Join-Path $work 'out' }
New-Item -ItemType Directory -Force -Path $work, $out | Out-Null
$utf8 = New-Object System.Text.UTF8Encoding($false)

# The page shows lines [__START__ - cap, __START__) as already said, then speaks up to __END__.
$template = @'
<!doctype html>
<html><head><meta charset="utf-8"><title>__TITLE__</title>
<style>
body { font: 13px "Segoe UI", sans-serif; background: #1f1f1f; color: #eee; margin: 10px; }
.fui-ChatMessageCompact__body span:first-child { display: block; font-weight: 600; font-size: 11px; }
</style></head><body>
<div role="group" aria-label="Live Captions" class="fui-Flex">
  <span class="fui-StyledText">Live Captions</span>
  <button id="captions-settings-menu-trigger-button-non-overflow" aria-label="Live Caption, Caption Settings">Settings</button>
  <div role="group" class="ui-box" id="list"></div>
</div>
<script>
var lines = __LINES__, cap = __CAP__, wordMs = __WORDMS__, start = __START__, end = __END__;
var list = document.getElementById('list');
function addRow(line) {
  var row = document.createElement('div');
  row.className = 'fui-ChatMessageCompact__body';
  row.setAttribute('role', 'group');
  var who = document.createElement('span'), said = document.createElement('span');
  who.textContent = line.s;
  row.appendChild(who);
  row.appendChild(said);
  list.appendChild(row);
  while (list.children.length > cap) list.removeChild(list.firstElementChild);
  return said;
}
for (var k = Math.max(0, start - cap); k < start; k++) addRow(lines[k]).textContent = lines[k].t;
var i = start;
function speak() {
  if (i >= end) return;
  var line = lines[i++], words = line.t.split(' ');
  var said = addRow(line);
  var n = 0;
  var tick = setInterval(function () {
    n++;
    if (n >= words.length) { said.textContent = line.t; clearInterval(tick); setTimeout(speak, wordMs); return; }
    var shown = words.slice(0, n);
    if (n % 3 === 2) shown[n - 1] = 'uh';
    said.textContent = shown.join(' ');
  }, wordMs);
}
setTimeout(speak, start > 0 ? 1000 : 3000);
</script></body></html>
'@

$speakers = 'Alex Rivera', 'Sam Lee', 'Jordan Park'
$vocab = ('so we need to look at the plan for next week and the draft the budget the schedule and ' +
    'the notes this that is a good point but I think it would be fine to move it to Friday').Split(' ')

function New-Lines([int]$Seed) {
    $rng = New-Object System.Random($Seed)
    for ($i = 0; $i -lt $Lines; $i++) {
        $speaker = $speakers[$rng.Next($speakers.Count)]
        if ($rng.NextDouble() -lt 0.25) { $text = @('Yeah.', 'OK.', 'Right.', 'Mm-hmm.')[$rng.Next(4)] }
        else {
            $words = @(for ($w = $rng.Next(2, 12); $w -gt 0; $w--) { $vocab[$rng.Next($vocab.Count)] })
            $words[0] = $words[0].Substring(0, 1).ToUpper() + $words[0].Substring(1)
            $text = ($words -join ' ') + '.'
        }
        [pscustomobject]@{ s = $speaker; t = $text }
    }
}

function Get-FakeWindow([string]$Title) {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)) {
        try { if ($w.Current.Name -eq $Title) { return $w } } catch { Write-Verbose "window vanished: $_" }
    }
    $null
}

function Wait-Until([scriptblock]$Condition, [int]$Seconds, [string]$What) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while (-not (& $Condition)) {
        if ((Get-Date) -gt $deadline) { throw "Timed out after $Seconds s waiting for: $What" }
        Start-Sleep -Milliseconds 300
    }
}

function Stop-TestEdge {
    Get-CimInstance Win32_Process -Filter "Name='msedge.exe'" |
        Where-Object { $_.CommandLine -and $_.CommandLine.Contains($work) } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}

# Closes a fake window like the user would. UI Automation's Close() sometimes did nothing
# here, so post WM_CLOSE, and end the test's Edge if the window is still there after 10 s.
function Close-FakeWindow([string]$Title) {
    $hwnd = [IntPtr](Get-FakeWindow $Title).Current.NativeWindowHandle
    [void][E2E.Win]::PostMessage($hwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    $deadline = (Get-Date).AddSeconds(10)
    while ([E2E.Win]::IsWindow($hwnd) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200 }
    if (-not [E2E.Win]::IsWindow($hwnd)) { return 'closed the window' }
    Stop-TestEdge
    'the window ignored WM_CLOSE, so the test ended its Edge'
}

# The saver's output (or the app's log) stays open while it runs, so read it with shared access.
function Get-SaverOutput {
    $path = Join-Path $work 'saver.out'
    if (-not (Test-Path -LiteralPath $path)) { return '' }
    $stream = New-Object System.IO.FileStream($path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    try { (New-Object System.IO.StreamReader($stream)).ReadToEnd() } finally { $stream.Dispose() }
}

function Get-Count([string]$Pattern) { ([regex]::Matches((Get-SaverOutput), $Pattern)).Count }

# Opens one fake captions window speaking lines [Start, End) and waits until it has said them.
# -Replaces closes another window once this one is up, as Teams does when you pin the pop-out.
function Show-FakeWindow([string]$WindowTitle, $Script, [int]$Start, [int]$End, [string]$Expect, [string]$Replaces, [switch]$KeepOpen) {
    $page = Join-Path $work (($WindowTitle -replace '[^\w]+', '-') + '.html')
    $html = $template.Replace('__TITLE__', $WindowTitle).Replace('__LINES__', (ConvertTo-Json -InputObject $Script -Compress)).
        Replace('__CAP__', "$Cap").Replace('__WORDMS__', "$WordMs").Replace('__START__', "$Start").Replace('__END__', "$End")
    [System.IO.File]::WriteAllText($page, $html, $utf8)
    $before = Get-Count $Expect
    $url = 'file:///' + ($page -replace '\\', '/')
    # The throttling switches keep a covered test window speaking (and its accessibility tree
    # current) while you use the PC; without them a hidden window can freeze mid-meeting.
    Start-Process -FilePath $edge -ArgumentList @("--user-data-dir=$work\profile", '--no-first-run', '--no-default-browser-check',
        '--disable-extensions', '--disable-sync', '--disable-backgrounding-occluded-windows', '--disable-renderer-backgrounding',
        '--disable-background-timer-throttling', '--window-size=420,640', "--app=$url") | Out-Null
    Wait-Until { Get-FakeWindow $WindowTitle } 30 "the window '$WindowTitle'"
    Write-Host "  opened '$WindowTitle'"
    if ($Replaces) { Write-Host "  $(Close-FakeWindow $Replaces) '$Replaces'" }
    Wait-Until { (Get-Count $Expect) -gt $before } 30 "the saver to log '$Expect'"
    Write-Host "  saver logged '$Expect'"
    $delay = if ($Start -gt 0) { 1000 } else { 3000 }
    $ms = $delay + (@($Script)[$Start..($End - 1)] | ForEach-Object { ($_.t.Split(' ').Count + 1) * $WordMs } | Measure-Object -Sum).Sum
    Start-Sleep -Milliseconds ($ms + 2500)
    if (-not $KeepOpen) { Write-Host "  $(Close-FakeWindow $WindowTitle)" }
}

# Plays one meeting. With -PinAt, its captions move to a pinned window partway through.
function Invoke-FakeMeeting([string]$Title, [int]$Seed, [int]$PinAt) {
    $script = @(New-Lines $Seed)
    if ($PinAt -gt 0) {
        Show-FakeWindow "$Title | Microsoft Teams" $script 0 $PinAt 'Saving captions from' -KeepOpen
        Show-FakeWindow "$Title | Microsoft Teams | Pinned window" $script $PinAt $script.Count 'Captions are back' -Replaces "$Title | Microsoft Teams"
    }
    else {
        Show-FakeWindow "$Title | Microsoft Teams" $script 0 $script.Count 'Saving captions from'
    }
    $script
}

# Speaker turns as the transcript writes them: consecutive lines from one speaker joined.
function Get-ExpectedTurns($Script) {
    $turns = New-Object System.Collections.Generic.List[string]
    $prev = $null
    foreach ($l in $Script) {
        if ($l.s -eq $prev) { $turns[$turns.Count - 1] += ' ' + $l.t }
        else { $turns.Add(('{0}: {1}' -f $l.s, $l.t)) }
        $prev = $l.s
    }
    , $turns
}

function Get-TranscriptTurns([string]$Path) {
    $turns = New-Object System.Collections.Generic.List[string]
    foreach ($line in [System.IO.File]::ReadAllLines($Path)) {
        if ($line -match '^\*\*\d\d:\d\d:\d\d (.+?):\*\* (.*)$') { $turns.Add(('{0}: {1}' -f $Matches[1], $Matches[2])) }
    }
    , $turns
}

function Test-Transcript([string]$Title, $Script) {
    $files = @(Get-ChildItem -LiteralPath $out -Filter "*$Title*.md")
    if ($files.Count -ne 1) { Write-Host "FAIL '$Title' has $($files.Count) transcripts, expected 1: $($files.Name -join ', ')"; return $false }
    $want = Get-ExpectedTurns $Script
    $got = Get-TranscriptTurns $files[0].FullName
    for ($i = 0; $i -lt [Math]::Max($want.Count, $got.Count); $i++) {
        $w = if ($i -lt $want.Count) { $want[$i] } else { '(nothing)' }
        $g = if ($i -lt $got.Count) { $got[$i] } else { '(nothing)' }
        if ($w -cne $g) {
            Write-Host "FAIL '$Title' turn $i of $($want.Count):"
            Write-Host "  expected: $w"
            Write-Host "  got:      $g"
            return $false
        }
    }
    Write-Host ("PASS '{0}': {1} lines in {2} speaker turns, one file, exact" -f $Title, $Script.Count, $want.Count)
    $true
}

$saverProc = $null
$ok = $true
try {
    if ($App) {
        # The app writes the same status lines to its log as the script prints.
        $appArgs = @('/process', 'msedge', '/window-title', '"Fake Teams meeting*"', '/out', "`"$out`"", '/log', "`"$work\saver.out`"",
            '/grace', '10', '/save', '1', '/raw', "`"$work\raw.jsonl`"", '/settings', "`"$work\settings.ini`"")
        if (-not $ShowApp) { $appArgs += '/tray' }
        $saverProc = Start-Process -FilePath $App -ArgumentList $appArgs -PassThru
    }
    else {
        $saverArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$saver`"", '-OutDir', "`"$out`"",
            '-ProcessName', 'msedge', '-WindowTitle', '"Fake Teams meeting*"', '-GraceSeconds', '10', '-SaveEverySeconds', '1',
            '-RawLog', "`"$work\raw.jsonl`"")
        if ($Watch) { $saverArgs += '-Watch' }
        if ($SaverVerbose) { $saverArgs += '-Verbose' }
        $saverProc = Start-Process -FilePath $saverHost -ArgumentList $saverArgs -NoNewWindow -PassThru `
            -RedirectStandardOutput "$work\saver.out" -RedirectStandardError "$work\saver.err"
    }

    $meetings = @(@{ Title = 'Fake Teams meeting A'; PinAt = [int]($Lines / 2) })
    if ($Watch) { $meetings += @{ Title = 'Fake Teams meeting B'; PinAt = 0 } }
    $seed = 0
    foreach ($m in $meetings) {
        $seed++
        Write-Host "Meeting '$($m.Title)':"
        $script = Invoke-FakeMeeting $m.Title $seed $m.PinAt
        Wait-Until { (Get-Count 'Saved \d+ lines') -ge $seed } $CloseWaitSeconds 'the saver to close the transcript'
        Write-Host '  saver noticed the window closed and saved the transcript'
        if (-not (Test-Transcript $m.Title $script)) { $ok = $false }
    }
    if (-not $Watch) {
        Wait-Until { $saverProc.HasExited } 15 'the saver to exit'
        Write-Host 'Saver exited by itself after the meeting.'
    }
    if ($App) {
        # The installer closes the app the same way: through its exit signal
        # (CaptionWatcher.LockName plus "-exit").
        $signal = [System.Threading.EventWaitHandle]::OpenExisting('Local\TeamsCaptionsSaver-msedge-exit')
        [void]$signal.Set()
        $signal.Dispose()
        Wait-Until { $saverProc.HasExited } 20 'the app to close'
        if ((Get-SaverOutput) -notmatch 'Teams Captions closed\.') { throw 'The app exited without logging a clean close.' }
        Write-Host 'PASS the app closed cleanly when signalled.'
    }
}
catch {
    $ok = $false
    Write-Host "FAIL $($_.Exception.Message)"
}
finally {
    if ($saverProc -and -not $saverProc.HasExited) { Stop-Process -Id $saverProc.Id -Force }
    Stop-TestEdge
    Write-Host '--- saver output'
    Write-Host (Get-SaverOutput).TrimEnd()
    $err = if (Test-Path -LiteralPath "$work\saver.err") { [System.IO.File]::ReadAllText("$work\saver.err").Trim() } else { '' }
    if ($err) { Write-Host "--- saver errors`n$err"; $ok = $false }
    if ($Keep -or -not $ok) { Write-Host "Kept $work" }
    else {
        Start-Sleep -Seconds 1
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}
if (-not $ok) { exit 1 }
