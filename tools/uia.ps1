# Drive the settings window from the Mac without clicks: tools/vm.sh uia <action> [name] [index]
#   select <radio name> | invoke <button name> [index] | list
param([string]$Action, [string]$Name, [int]$Index = 0)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$root = [System.Windows.Automation.AutomationElement]::RootElement
$win = $root.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition) |
    Where-Object { $_.Current.Name -like "WhisperShortcut*Einstellungen" } | Select-Object -First 1
if (-not $win) { "no settings window"; exit 1 }
$all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
if ($Action -eq "list") { foreach ($e in $all) { if ($e.Current.Name) { "{0} | {1} | enabled={2} offscreen={3}" -f $e.Current.ControlType.ProgrammaticName, $e.Current.Name, $e.Current.IsEnabled, $e.Current.IsOffscreen } }; exit 0 }
$hits = @($all | Where-Object { $_.Current.Name -eq $Name })
if ($hits.Count -le $Index) { "not found: $Name"; exit 1 }
$el = $hits[$Index]
if ($Action -eq "select") { $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); "selected $Name" }
elseif ($Action -eq "invoke") { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); "invoked $Name" }
