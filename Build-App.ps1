<#
.SYNOPSIS
    Builds the Teams Captions app (bin\TeamsCaptions.exe), and installs or removes it.

.DESCRIPTION
    Uses the C# compiler that comes with Windows (.NET Framework 4.8), so nothing else needs
    installing, and the exe runs on any Windows 10 or 11 PC. No admin rights are needed.

    -Install copies the exe to %LOCALAPPDATA%\Programs\Teams Captions and puts a "Teams
    Captions" shortcut on the desktop. A running copy is asked to close first, which saves
    its transcript.

    -Uninstall closes the app and removes it, its shortcuts, its settings and its log.
    Transcripts are never touched.

.PARAMETER Install
    Copy the app to %LOCALAPPDATA%\Programs\Teams Captions and make the desktop shortcut.

.PARAMETER StartAtSignIn
    With -Install: also start the app in the notification area when you sign in.

.PARAMETER Launch
    With -Install: start the installed app now, in the notification area.

.PARAMETER Uninstall
    Remove the installed app instead of building.

.EXAMPLE
    .\Build-App.ps1 -Install -StartAtSignIn -Launch
#>
param(
    [switch]$Install,
    [switch]$StartAtSignIn,
    [switch]$Launch,
    [switch]$Uninstall
)
$ErrorActionPreference = 'Stop'

$dest = Join-Path $env:LOCALAPPDATA 'Programs\Teams Captions'
$target = Join-Path $dest 'TeamsCaptions.exe'
$desktopLink = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Teams Captions.lnk'
$signInLink = Join-Path ([Environment]::GetFolderPath('Startup')) 'Teams Captions.lnk'

# Asks a running copy to close (it saves its transcript) and waits for it. The signal's name
# is CaptionWatcher.LockName("ms-teams") plus "-exit".
function Close-RunningApp {
    $signal = $null
    if (-not [System.Threading.EventWaitHandle]::TryOpenExisting('Local\TeamsCaptionsSaver-ms-teams-exit', [ref]$signal)) { return }
    [void]$signal.Set()
    $signal.Dispose()
    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Process -Name TeamsCaptions -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 300 }
    if (Get-Process -Name TeamsCaptions -ErrorAction SilentlyContinue) { throw 'The running Teams Captions did not close; close it from its notification-area menu and try again.' }
    'Closed the running Teams Captions.'
}

if ($Uninstall) {
    Close-RunningApp
    $data = Join-Path $env:LOCALAPPDATA 'Teams Captions'
    foreach ($path in $desktopLink, $signInLink, $dest, (Join-Path $env:APPDATA 'Teams Captions'),
        (Join-Path $data 'TeamsCaptions.log'), (Join-Path $data 'TeamsCaptions.log.old')) {
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Recurse -Force
            "Removed $path"
        }
    }
    # Transcripts that could not be saved where they belonged are kept.
    $unsaved = Join-Path $data 'Unsaved'
    if (Test-Path -LiteralPath $unsaved) { "Kept $unsaved (transcripts that could not be saved in their folder)." }
    elseif (Test-Path -LiteralPath $data) { Remove-Item -LiteralPath $data -Recurse -Force }
    'Teams Captions is uninstalled. Your transcripts are where they were.'
    return
}

$fw = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319'
$bin = Join-Path $PSScriptRoot 'bin'
$exe = Join-Path $bin 'TeamsCaptions.exe'
$app = Join-Path $PSScriptRoot 'app'
$src = Join-Path $PSScriptRoot 'src'
New-Item -ItemType Directory -Force -Path $bin | Out-Null

$refs = 'System.dll', 'System.Core.dll', 'System.Xml.dll', 'System.Xaml.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll',
    'WPF\WindowsBase.dll', 'WPF\PresentationCore.dll', 'WPF\PresentationFramework.dll', 'WPF\UIAutomationClient.dll', 'WPF\UIAutomationTypes.dll' |
    ForEach-Object { '/reference:' + (Join-Path $fw $_) }
$cscArgs = @('/nologo', '/target:winexe', '/optimize+', '/warn:4', "/out:$exe",
    "/win32icon:$app\TeamsCaptions.ico", "/win32manifest:$app\app.manifest",
    "/resource:$app\MainWindow.xaml,TeamsCaptions.MainWindow.xaml",
    "/resource:$app\TeamsCaptions.ico,TeamsCaptions.TeamsCaptions.ico") + $refs +
    @("$src\CaptionLog.cs", "$src\CaptionWatcher.cs", "$src\Settings.cs", "$app\App.cs")
& (Join-Path $fw 'csc.exe') @cscArgs
if ($LASTEXITCODE -ne 0) { throw "The build failed (csc exit code $LASTEXITCODE)." }
"Built $exe"
if (-not $Install) { return }

function New-Shortcut([string]$Path, [string]$Target, [string]$Arguments, [string]$Description) {
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($Path)
    $link.TargetPath = $Target
    $link.Arguments = $Arguments
    $link.WorkingDirectory = Split-Path $Target
    $link.Description = $Description
    $link.Save()
}

Close-RunningApp
New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item -LiteralPath $exe -Destination $target -Force
"Installed $target"

New-Shortcut $desktopLink $target '' 'Saves Teams live captions for later review'
"Desktop shortcut: $desktopLink"

if ($StartAtSignIn) {
    New-Shortcut $signInLink $target '/tray' 'Saves Teams live captions from every meeting'
    "Starts at sign-in: $signInLink"
}

if ($Launch) {
    # Start it through Explorer so it runs on its own, like it does from the shortcut.
    $starter = if ($StartAtSignIn) { $signInLink } else { $desktopLink }
    & explorer.exe $starter
    "Started Teams Captions."
}
