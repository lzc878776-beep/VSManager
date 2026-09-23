<#
  VSManager skill helper: controls every running Visual Studio through the local VSManager API.
  Usage:
	vsm.ps1 list
	vsm.ps1 send   <vs> "<task text>" [-Wait] [-Timeout 600]
	vsm.ps1 wait   <vs> [-Timeout 600]
	vsm.ps1 reply  <vs>
	vsm.ps1 chat   <vs>
	vsm.ps1 debug  <vs> <go|run|break|stop|restart|build|rebuild|cancelbuild|stepover|stepinto|stepout>
	vsm.ps1 errors <vs> [-Max 50]
	vsm.ps1 stop   <vs>
	vsm.ps1 new    <vs>
	vsm.ps1 dock
	vsm.ps1 note   <vs> ["<role description>"] [-Clear]
  <vs> = index shown by "list" (1, 2, ...), "#2", a PID via -VsPid, or part of the VS name.
#>
param(
	[Parameter(Position = 0)][string]$Command = "list",
	[Parameter(Position = 1)][string]$Vs,
	[Parameter(Position = 2, ValueFromRemainingArguments = $true)][string[]]$Rest,
	[int]$VsPid = 0,
	[switch]$Wait,
	[int]$Timeout = 600,
	[int]$Max = 50,
	[switch]$Json,
	[switch]$Clear
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$settingsPath = if ($env:VSMANAGER_SETTINGS) { $env:VSMANAGER_SETTINGS } else { Join-Path $env:APPDATA "VSManager\settings.json" }
if (-not (Test-Path $settingsPath)) { Write-Error "VSManager settings not found ($settingsPath). Start VSManager first."; exit 2 }
$cfg = Get-Content $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
if (-not $cfg.WebEnabled) { Write-Error "VSManager API is disabled. Enable 'Settings > 手机网页遥控' in VSManager (or reinstall the skill from VSManager)."; exit 2 }
$port = if ($cfg.WebPort -ge 1024) { $cfg.WebPort } else { 8765 }
$base = "http://127.0.0.1:$port/api"
$headers = @{ "X-Key" = $cfg.WebToken; "X-Client" = "AI Skill" }

function Call([string]$path, [hashtable]$query, [hashtable]$body, [int]$timeoutSec = 60) {
	$url = "$base/$path"
	if ($query) {
		$pairs = $query.GetEnumerator() | Where-Object { $null -ne $_.Value -and "$($_.Value)" -ne "" } |
			ForEach-Object { [Uri]::EscapeDataString($_.Key) + "=" + [Uri]::EscapeDataString("$($_.Value)") }
		if ($pairs) { $url += "?" + ($pairs -join "&") }
	}
	try {
		if ($body) {
			$bytes = [System.Text.Encoding]::UTF8.GetBytes(($body | ConvertTo-Json -Compress))
			return Invoke-RestMethod -Method Post -Uri $url -Headers $headers -Body $bytes -ContentType "application/json; charset=utf-8" -TimeoutSec $timeoutSec
		}
		return Invoke-RestMethod -Method Get -Uri $url -Headers $headers -TimeoutSec $timeoutSec
	}
	catch {
		Write-Error ("VSManager API call failed ($path): " + $_.Exception.Message + ". Is VSManager running?")
		exit 1
	}
}

function Target() {
	$q = @{}
	if ($VsPid -gt 0) { $q.pid = $VsPid } elseif ($Vs) { $q.vs = $Vs } else { Write-Error "Specify a VS (index, name or -VsPid). Run 'vsm.ps1 list' first."; exit 2 }
	return $q
}

function Emit($r, [string]$field = "msg") {
	if ($Json) { $r | ConvertTo-Json -Depth 8; return }
	if ($r.ok -eq $false) { Write-Output ("ERROR: " + $r.msg); exit 1 }
	Write-Output $r.$field
}

switch ($Command.ToLowerInvariant()) {
	"list" {
		$r = Call "state"
		if ($Json) { $r | ConvertTo-Json -Depth 5; break }
		if (-not $r.vs -or $r.vs.Count -eq 0) { "No Visual Studio instance is running."; break }
		foreach ($v in $r.vs) {
			$copilot = switch ($v.copilot) { "busy" { "Copilot busy" } "idle" { "Copilot idle" } default { "Copilot pane not found" } }
			"#{0}  {1}  (pid {2})  [{3}]  {4}" -f $v.idx, $v.name, $v.pid, $copilot, $v.dbg
			if ($v.note) { "    role: " + $v.note }
			if ($v.sln) { "    solution: " + $v.sln }
		}
	}
	"send" {
		$text = ($Rest -join " ").Trim()
		if (-not $text) { Write-Error "Message text is empty."; exit 2 }
		$q = Target
		$body = @{ text = $text }
		foreach ($k in $q.Keys) { $body[$k] = $q[$k] }
		$r = Call "send" $null $body 120
		if ($r.ok -eq $false -or -not $Wait) { Emit $r; break }
		Write-Output $r.msg
		$w = Call "wait" ($q + @{ timeout = $Timeout }) $null ($Timeout + 60)
		if ($Json) { $w | ConvertTo-Json -Depth 5; break }
		if ($w.ok -eq $false) { Write-Output ("ERROR: " + $w.msg); exit 1 }
		"--- Copilot finished in $($w.seconds)s. Reply: ---"
		$w.reply
	}
	"wait" {
		$w = Call "wait" ((Target) + @{ timeout = $Timeout }) $null ($Timeout + 60)
		if ($Json) { $w | ConvertTo-Json -Depth 5; break }
		if ($w.ok -eq $false) { Write-Output ("ERROR: " + $w.msg); exit 1 }
		if ($w.completed) { "--- Copilot finished in $($w.seconds)s. Reply: ---" } else { "--- Copilot is idle. Last reply: ---" }
		$w.reply
	}
	"reply" { Emit (Call "reply" (Target)) "reply" }
	"chat" {
		$r = Call "chat" (Target)
		if ($Json -or $r.ok -eq $false) { Emit $r; break }
		if (-not $r.found) { "Copilot chat pane not found in this VS (try: vsm.ps1 dock)." }
		foreach ($m in $r.messages) {
			$who = if ($m.role -eq "a") { "Copilot" } else { "User" }
			"### $who"
			foreach ($p in $m.parts) { if ($p.s) { "  > " + $p.t } else { $p.t } }
			""
		}
	}
	"debug" {
		$action = if ($Rest) { $Rest[0] } else { "" }
		if (-not $action) { Write-Error "Specify an action: go run break stop restart build rebuild cancelbuild stepover stepinto stepout"; exit 2 }
		$body = @{ action = $action.ToLowerInvariant() }
		$q = Target; foreach ($k in $q.Keys) { $body[$k] = $q[$k] }
		Emit (Call "debug" $null $body 120)
	}
	"errors" { Emit (Call "errors" ((Target) + @{ max = $Max }) $null 120) }
	"stop" { $b = Target; $b.id = "CancelButton"; Emit (Call "button" $null $b) }
	"new" { $b = Target; $b.id = "createNewThread"; Emit (Call "button" $null $b) }
	"dock" { Emit (Call "dock" $null @{ all = $true } 180) }
	"note" {
		$text = ($Rest -join " ").Trim()
		$q = Target
		if (-not $text -and -not $Clear) {
			$r = Call "state"
			$v = $r.vs | Where-Object { ($q.pid -and $_.pid -eq $q.pid) -or ("$($_.idx)" -eq "$($q.vs)".TrimStart('#')) -or ($q.vs -and $_.name -like "*$($q.vs)*") } | Select-Object -First 1
			if (-not $v) { Write-Output "ERROR: VS not found"; exit 1 }
			if ($v.note) { $v.note } else { "(no role description yet)" }
			break
		}
		$body = @{ note = $(if ($Clear) { "" } else { $text }) }
		foreach ($k in $q.Keys) { $body[$k] = $q[$k] }
		Emit (Call "note" $null $body)
	}
	default { Write-Error "Unknown command '$Command'. Commands: list send wait reply chat debug errors stop new dock note"; exit 2 }
}
