<#
.SYNOPSIS
    Shows how Teams lays out its live captions, to fix the saver after a Teams update.

.DESCRIPTION
    Lists every element in the Teams windows whose name, class or automation id matches
    -Pattern, then outlines the first match: control type, class, automation id and the
    start of each name. Run it during a meeting with live captions on.

    src\CaptionWatcher.cs expects caption rows with the class fui-ChatMessageCompact__body
    (speaker text, then the words) under controls whose automation ids start "captions-".

.EXAMPLE
    .\Show-CaptionTree.ps1
#>
param(
    [string]$Pattern = 'caption',
    [string]$ProcessName = 'ms-teams',
    [int]$NameLength = 50,
    [int]$OutlineLines = 60
)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$AE = [System.Windows.Automation.AutomationElement]

function Get-Short([string]$Text, [int]$Length) {
    if ($null -eq $Text) { return '' }
    $Text = $Text -replace '\s+', ' '
    if ($Text.Length -gt $Length) { $Text.Substring(0, $Length) + '...' } else { $Text }
}

function Format-Element($Element, [int]$Indent) {
    $c = $Element.Cached
    '{0}{1} class="{2}" id="{3}" name="{4}"' -f ('  ' * $Indent), ($c.ControlType.ProgrammaticName -replace '^ControlType\.', ''),
        (Get-Short $c.ClassName 60), $c.AutomationId, (Get-Short $c.Name $NameLength)
}

$request = New-Object System.Windows.Automation.CacheRequest
$request.TreeScope = [System.Windows.Automation.TreeScope]::Subtree
$request.TreeFilter = [System.Windows.Automation.Automation]::RawViewCondition
foreach ($p in $AE::NameProperty, $AE::ClassNameProperty, $AE::AutomationIdProperty, $AE::ControlTypeProperty) { $request.Add($p) }

foreach ($proc in @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue)) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $proc.Id)
    foreach ($window in $AE::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)) {
        $timer = [Diagnostics.Stopwatch]::StartNew()
        $tree = $window.GetUpdatedCache($request)
        $nodes = 0
        $hits = New-Object System.Collections.Generic.List[object]
        $stack = New-Object System.Collections.Stack
        $stack.Push(@($tree, 0))
        while ($stack.Count) {
            $e, $depth = $stack.Pop()
            $nodes++
            $c = $e.Cached
            if ("$($c.Name)|$($c.ClassName)|$($c.AutomationId)" -match $Pattern) { $hits.Add(@($e, $depth)) }
            $kids = $e.CachedChildren
            for ($i = $kids.Count - 1; $i -ge 0; $i--) { $stack.Push(@($kids[$i], ($depth + 1))) }
        }
        '=== "{0}": {1} elements read in {2} ms, {3} matching "{4}"' -f $window.Current.Name, $nodes, $timer.ElapsedMilliseconds, $hits.Count, $Pattern
        foreach ($hit in $hits) { '  depth {0}: {1}' -f $hit[1], (Format-Element $hit[0] 0).Trim() }
        if ($hits.Count) {
            '  --- outline under the first match'
            $lines = 0
            $stack = New-Object System.Collections.Stack
            $stack.Push(@($hits[0][0], 1))
            while ($stack.Count -and $lines -lt $OutlineLines) {
                $e, $depth = $stack.Pop()
                Format-Element $e $depth
                $lines++
                $kids = $e.CachedChildren
                for ($i = $kids.Count - 1; $i -ge 0; $i--) { $stack.Push(@($kids[$i], ($depth + 1))) }
            }
        }
    }
}
