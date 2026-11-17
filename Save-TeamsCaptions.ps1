<#
.SYNOPSIS
    Saves Microsoft Teams live captions to a Markdown transcript for later review.

.DESCRIPTION
    The console version of the Teams Captions app; both run the same code in src\.

    Reads the live captions that new Teams shows during a meeting (popped out, or in the
    meeting window) through Windows UI Automation, and writes them with speaker names and
    times to "<OutDir>\<yyyy-MM-dd HHmm> <meeting title>.md".

    The file is rewritten every few seconds while captions arrive, so it stays current even
    if this window is closed. Teams keeps only the most recent captions on screen, so start
    this before the part of the meeting you want to keep.

    Turn on live captions in the meeting first (More > Language and speech).

.PARAMETER OutDir
    Folder for the transcripts. Default: Documents\Teams captions.

.PARAMETER Watch
    Keep running after a meeting ends, and save the next meeting's captions too.

.PARAMETER PollMs
    How often to read the captions, in milliseconds.

.PARAMETER SaveEverySeconds
    Longest time new captions wait before the file is rewritten.

.PARAMETER GraceSeconds
    How long the captions may be gone (pop-out closed, captions off) before the transcript
    is closed. If they come back sooner, the same file carries on.

.PARAMETER RawLog
    For troubleshooting: also append every change seen on screen to this file, one JSON
    object per line, so a transcript can be rebuilt or the merge tested offline.

.PARAMETER FromRawLog
    Rebuild transcripts from a file written with -RawLog, save them in OutDir, and stop.

.PARAMETER WindowTitle
    Only look in windows whose title matches this wildcard, e.g. 'Weekly*'.

.PARAMETER ProcessName
    Process whose windows hold the captions. Default ms-teams (new Teams); the tests point
    it at a browser showing a fake captions page.

.EXAMPLE
    .\Save-TeamsCaptions.ps1

.EXAMPLE
    .\Save-TeamsCaptions.ps1 -Watch -OutDir D:\Notes\Captions
#>
[CmdletBinding()]
param(
    [string]$OutDir,
    [switch]$Watch,
    [ValidateRange(250, 10000)][int]$PollMs = 1000,
    [ValidateRange(1, 600)][int]$SaveEverySeconds = 5,
    [ValidateRange(0, 86400)][int]$GraceSeconds = 180,
    [string]$RawLog,
    [string]$FromRawLog,
    [string]$WindowTitle = '*',
    [string]$ProcessName = 'ms-teams'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
if (-not ('TeamsCaptions.CaptionWatcher' -as [type])) {
    $src = Join-Path $PSScriptRoot 'src'
    $refs = @([System.Windows.Automation.AutomationElement].Assembly.Location,
        [System.Windows.Automation.ControlType].Assembly.Location)
    # PowerShell 7 drops its default references once any are given, so pass those too.
    if ($PSVersionTable.PSEdition -eq 'Core') { $refs += @(Get-ChildItem (Join-Path $PSHOME 'ref') -Filter *.dll | ForEach-Object FullName) }
    Add-Type -Path (Join-Path $src 'CaptionLog.cs'), (Join-Path $src 'CaptionWatcher.cs') -ReferencedAssemblies $refs
}

function Write-Status([string]$Message) { Write-Host ('{0:HH:mm:ss}  {1}' -f (Get-Date), $Message) }

function Set-ConsoleTitle([string]$Text) {
    try { $Host.UI.RawUI.WindowTitle = $Text } catch { Write-Verbose "No console title: $_" }
}

if (-not $OutDir) { $OutDir = [TeamsCaptions.TranscriptFile]::DefaultFolder }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

if ($FromRawLog) {
    # Replay a raw log: one transcript per run of lines with the same meeting title.
    function Save-Rebuilt($Rebuilt) {
        if ($Rebuilt.Log.Count -eq 0) { return }
        [TeamsCaptions.TranscriptFile]::Write($Rebuilt.Path, $Rebuilt.Log, $Rebuilt.Title)
        Write-Status ('Saved {0} lines to {1}' -f $Rebuilt.Log.Count, $Rebuilt.Path)
    }
    $current = $null
    foreach ($line in [System.IO.File]::ReadAllLines($FromRawLog)) {
        if (-not $line.Trim()) { continue }
        $record = $line | ConvertFrom-Json
        $time = [datetime]$record.t
        if ($record.PSObject.Properties['event']) {
            if ($current) { $current.Log.Break() }
            continue
        }
        if (-not $current -or $current.Title -ne $record.title) {
            if ($current) { Save-Rebuilt $current }
            $current = [pscustomobject]@{
                Title = $record.title
                Path  = [TeamsCaptions.TranscriptFile]::NewPath($OutDir, $record.title, $time)
                Log   = New-Object TeamsCaptions.CaptionLog
            }
        }
        [void]$current.Log.Merge([string[]]@($record.speakers), [string[]]@($record.texts), $time)
    }
    if ($current) { Save-Rebuilt $current }
    return
}

# One saver at a time (this script or the app): a second would write a second file for
# every meeting.
$instance = New-Object System.Threading.Mutex($false, [TeamsCaptions.CaptionWatcher]::LockName($ProcessName))
$owned = $false
try { $owned = $instance.WaitOne(0) }
catch {
    # A copy that was killed leaves the lock abandoned; taking it over is fine.
    if ($_.Exception -isnot [System.Threading.AbandonedMutexException] -and
        $_.Exception.InnerException -isnot [System.Threading.AbandonedMutexException]) { throw }
    $owned = $true
}
if (-not $owned) {
    Write-Status 'Teams captions are already being saved (the Teams Captions app, or another console window). This one will close.'
    Start-Sleep -Seconds 8
    return
}

$watcher = New-Object TeamsCaptions.CaptionWatcher
$watcher.OutDir = $OutDir
$watcher.ProcessName = $ProcessName
$watcher.WindowTitle = $WindowTitle
$watcher.GraceSeconds = $GraceSeconds
$watcher.SaveEverySeconds = $SaveEverySeconds
$watcher.Watch = [bool]$Watch
if ($RawLog) { $watcher.RawLogPath = $RawLog }
$watcher.add_Message({ param($Message) Write-Status $Message })
$watcher.add_Trace({ param($Message) Write-Verbose ('{0:HH:mm:ss.fff} {1}' -f (Get-Date), $Message) })
$watcher.add_Changed({
        param($Status)
        $title = switch ($Status.State.ToString()) {
            'Saving' { 'Teams captions: {0} lines - {1}' -f $Status.Lines, $Status.Title }
            'Away' { 'Teams captions: paused, {0} lines - {1}' -f $Status.Lines, $Status.Title }
            default { 'Teams captions: waiting' }
        }
        Set-ConsoleTitle $title
    })

Write-Status "Teams live captions will be saved in $OutDir"
Write-Status 'Waiting for live captions in a Teams meeting. Press Ctrl+C or close this window to stop.'
Set-ConsoleTitle 'Teams captions: waiting'

try {
    while (-not $watcher.Finished) {
        $watcher.Poll((Get-Date))
        Start-Sleep -Milliseconds $PollMs
    }
}
finally {
    $watcher.Shutdown()
    $instance.ReleaseMutex()
}
