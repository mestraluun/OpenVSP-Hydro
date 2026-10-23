<#
    AllowList Firewall - a small Windows 10 GUI on top of Windows Defender Firewall.

    Blocks ALL outbound (and optionally inbound) traffic except for the programs
    you allow (by default: Claude Code / Claude desktop, Google Chrome, OpenAI
    Codex, OpenCode).

    Requires Windows PowerShell 5.1 (built into Windows 10) and admin rights.
    The script relaunches itself elevated if needed.
#>
param(
    [string]$UserProfileDir = $env:USERPROFILE,
    [string]$LocalAppDataDir = $env:LOCALAPPDATA,
    [string]$AppDataDir = $env:APPDATA
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------- elevation --
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    # Pass the *current* user's folders along so auto-detect still finds apps
    # installed per-user when a different admin account is used for UAC.
    $argList = @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"",
        '-UserProfileDir', "`"$UserProfileDir`"",
        '-LocalAppDataDir', "`"$LocalAppDataDir`"",
        '-AppDataDir', "`"$AppDataDir`""
    )
    Start-Process -FilePath 'powershell.exe' -ArgumentList $argList -Verb RunAs
    exit
}

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

# ------------------------------------------------------------------ globals --
$RuleGroup   = 'AllowList Firewall'
$CoreNetGroup = '@FirewallAPI.dll,-25000'   # "Core Networking" (DNS, DHCP, IPv6 NDP...)
$DataDir     = Join-Path $env:ProgramData 'AllowListFirewall'
$ConfigPath  = Join-Path $DataDir 'apps.json'
$StatePath   = Join-Path $DataDir 'state.json'
$Svchost     = Join-Path $env:SystemRoot 'System32\svchost.exe'

if (-not (Test-Path $DataDir)) { New-Item -ItemType Directory -Path $DataDir | Out-Null }

# ------------------------------------------------------------------ helpers --
function Read-Json($path) {
    if (-not (Test-Path $path)) { return $null }
    try { return (Get-Content -Raw -Path $path | ConvertFrom-Json) } catch { return $null }
}

function Write-Json($path, $obj) {
    ConvertTo-Json -InputObject $obj -Depth 5 | Set-Content -Path $path -Encoding UTF8
}

function Get-State {
    $s = Read-Json $StatePath
    if ($null -eq $s) {
        return [pscustomobject]@{ Active = $false; DisabledRules = @(); Profiles = @(); Backup = '' }
    }
    return $s
}

function Find-Exe([string[]]$roots, [string[]]$names, [int]$depth = 8) {
    $found = @()
    foreach ($r in $roots) {
        if (-not $r -or -not (Test-Path $r)) { continue }
        $found += Get-ChildItem -Path $r -Recurse -Depth $depth -File -Include $names -ErrorAction SilentlyContinue |
                  Select-Object -ExpandProperty FullName
    }
    return $found | Select-Object -Unique
}

function Find-Candidates {
    $U  = $UserProfileDir; $L = $LocalAppDataDir; $A = $AppDataDir
    $PF = $env:ProgramFiles; $PF86 = ${env:ProgramFiles(x86)}
    $out = New-Object System.Collections.ArrayList

    function Add-Cand($name, $path, $checked) {
        if ($path -and (Test-Path $path) -and -not ($out | Where-Object { $_.Path -eq $path })) {
            [void]$out.Add([pscustomobject]@{ Name = $name; Path = (Resolve-Path $path).Path; Enabled = $checked })
        }
    }

    # Google Chrome (system-wide or per-user install)
    foreach ($p in @("$PF\Google\Chrome\Application\chrome.exe",
                     "$PF86\Google\Chrome\Application\chrome.exe",
                     "$L\Google\Chrome\Application\chrome.exe")) { Add-Cand 'Google Chrome' $p $true }

    # Package-manager folders where CLI tools usually live
    $toolRoots = @("$A\npm\node_modules", "$L\pnpm", "$U\.bun\install\global\node_modules",
                   "$U\scoop\apps", "$U\.local\bin", "$U\.local\share\claude",
                   "$U\.opencode\bin", "$U\.cargo\bin", "$L\Programs", "$L\Microsoft\WinGet\Packages")

    # Claude Code (native installer -> ~\.local\bin\claude.exe) and Claude desktop app
    Add-Cand 'Claude Code' "$U\.local\bin\claude.exe" $true
    foreach ($p in (Find-Exe ($toolRoots + "$L\AnthropicClaude") @('claude.exe'))) { Add-Cand 'Claude' $p $true }

    # OpenAI Codex CLI (npm package ships a native codex.exe that does the networking)
    foreach ($p in (Find-Exe $toolRoots @('codex.exe'))) { Add-Cand 'Codex' $p $true }

    # OpenCode (npm/scoop/installer all ship a native opencode.exe)
    foreach ($p in (Find-Exe $toolRoots @('opencode.exe'))) { Add-Cand 'OpenCode' $p $true }

    # Also check anything on PATH
    foreach ($n in @('claude', 'codex', 'opencode')) {
        foreach ($c in (Get-Command $n -All -CommandType Application -ErrorAction SilentlyContinue)) {
            if ($c.Source -like '*.exe') { Add-Cand ((Get-Culture).TextInfo.ToTitleCase($n)) $c.Source $true }
        }
    }

    # Node.js: only required when Claude Code was installed via npm (runs as node.exe).
    # Allowing node.exe lets ANY node script reach the internet, so only pre-check it
    # when there is no native claude.exe.
    $npmClaude = Test-Path "$A\npm\node_modules\@anthropic-ai\claude-code"
    $hasNativeClaude = [bool]($out | Where-Object { $_.Path -like '*\claude.exe' })
    foreach ($c in (Get-Command node -All -CommandType Application -ErrorAction SilentlyContinue)) {
        if ($c.Source -like '*.exe') {
            Add-Cand 'Node.js (npm Claude Code - allows ANY node script)' $c.Source ($npmClaude -and -not $hasNativeClaude)
        }
    }
    return $out
}

# ------------------------------------------------------------ firewall ops --
function Remove-OurRules {
    Get-NetFirewallRule -Group $RuleGroup -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue
}

function Invoke-Lockdown($apps, [bool]$keepCore, [bool]$blockInbound) {
    $state = Get-State
    $disabled = New-Object System.Collections.ArrayList
    foreach ($n in @($state.DisabledRules)) { if ($n) { [void]$disabled.Add($n) } }

    if (-not $state.Active) {
        # First lockdown: full backup + remember original profile settings.
        $backup = Join-Path $DataDir ("backup-{0:yyyyMMdd-HHmmss}.wfw" -f (Get-Date))
        & netsh advfirewall export "$backup" | Out-Null
        Log "Full firewall backup saved: $backup"
        $state.Backup = $backup
        $state.Profiles = @(Get-NetFirewallProfile -PolicyStore PersistentStore | ForEach-Object {
            [pscustomobject]@{
                Name = $_.Name; Enabled = "$($_.Enabled)"
                DefaultOutboundAction = "$($_.DefaultOutboundAction)"
                DefaultInboundAction  = "$($_.DefaultInboundAction)"
                AllowInboundRules     = "$($_.AllowInboundRules)"
            }
        })
    }

    # Every other enabled outbound ALLOW rule would punch a hole, so disable them
    # (recorded so "Restore" can re-enable exactly these).
    $others = Get-NetFirewallRule -PolicyStore PersistentStore -Direction Outbound -Action Allow -Enabled True -ErrorAction SilentlyContinue |
              Where-Object { $_.Group -ne $RuleGroup -and -not ($keepCore -and $_.Group -eq $CoreNetGroup) }
    $count = 0
    foreach ($r in $others) {
        try {
            Disable-NetFirewallRule -Name $r.Name -PolicyStore PersistentStore
            if (-not $disabled.Contains($r.Name)) { [void]$disabled.Add($r.Name) }
            $count++
        } catch { Log "  could not disable '$($r.DisplayName)': $($_.Exception.Message)" }
    }
    Log "Disabled $count other outbound allow rule(s)."

    Remove-OurRules
    foreach ($app in $apps) {
        if (-not (Test-Path $app.Path)) { Log "  SKIP (missing): $($app.Path)"; continue }
        New-NetFirewallRule -DisplayName "AllowList: $($app.Name)" -Group $RuleGroup -Direction Outbound `
            -Action Allow -Program $app.Path -Profile Any -Enabled True | Out-Null
        Log "  ALLOW  $($app.Name)  ->  $($app.Path)"
    }

    if ($keepCore) {
        # Make sure name resolution + DHCP keep working even if Core Networking was missing.
        foreach ($proto in 'UDP', 'TCP') {
            New-NetFirewallRule -DisplayName "AllowList: DNS client ($proto)" -Group $RuleGroup -Direction Outbound `
                -Action Allow -Protocol $proto -RemotePort 53 -Program $Svchost -Service Dnscache -Profile Any | Out-Null
        }
        New-NetFirewallRule -DisplayName 'AllowList: DHCP client' -Group $RuleGroup -Direction Outbound `
            -Action Allow -Protocol UDP -LocalPort 68 -RemotePort 67 -Program $Svchost -Service Dhcp -Profile Any | Out-Null
        Log '  ALLOW  DNS / DHCP / Windows Core Networking'
    }

    Set-NetFirewallProfile -All -PolicyStore PersistentStore -Enabled True `
        -DefaultOutboundAction Block -DefaultInboundAction Block -AllowInboundRules ($(if ($blockInbound) { 'False' } else { 'True' }))
    Log ("All profiles: outbound = BLOCK, inbound = BLOCK" + $(if ($blockInbound) { ' (including allowed inbound rules)' } else { '' }))

    $state.Active = $true
    $state.DisabledRules = @($disabled)
    Write-Json $StatePath $state
    Log 'Lockdown ACTIVE.'
}

function Invoke-Restore {
    $state = Get-State
    if (-not $state.Active) { Log 'Lockdown is not active - nothing to restore.'; return }

    Remove-OurRules
    $ok = 0
    foreach ($n in @($state.DisabledRules)) {
        try { Enable-NetFirewallRule -Name $n -PolicyStore PersistentStore -ErrorAction Stop; $ok++ } catch { }
    }
    Log "Re-enabled $ok rule(s)."

    foreach ($p in @($state.Profiles)) {
        Set-NetFirewallProfile -Name $p.Name -PolicyStore PersistentStore -Enabled $p.Enabled `
            -DefaultOutboundAction $p.DefaultOutboundAction -DefaultInboundAction $p.DefaultInboundAction `
            -AllowInboundRules $p.AllowInboundRules
    }
    Log 'Profile defaults restored.'

    $state.Active = $false
    $state.DisabledRules = @()
    Write-Json $StatePath $state
    Log 'Lockdown OFF - normal firewall behaviour restored.'
}

function Invoke-BackupImport {
    $dlg = New-Object System.Windows.Forms.OpenFileDialog
    $dlg.InitialDirectory = $DataDir
    $dlg.Filter = 'Firewall backup (*.wfw)|*.wfw'
    if ($dlg.ShowDialog() -ne 'OK') { return }
    $msg = "Import '$($dlg.FileName)'?`n`nThis replaces ALL current firewall rules and settings with the backup."
    if ([System.Windows.Forms.MessageBox]::Show($msg, 'Emergency restore', 'YesNo', 'Warning') -ne 'Yes') { return }
    & netsh advfirewall import "$($dlg.FileName)" | Out-Null
    Write-Json $StatePath ([pscustomobject]@{ Active = $false; DisabledRules = @(); Profiles = @(); Backup = '' })
    Log "Imported backup $($dlg.FileName)."
}

# ---------------------------------------------------------------------- GUI --
$form = New-Object System.Windows.Forms.Form
$form.Text = 'AllowList Firewall'
$form.Size = New-Object System.Drawing.Size(900, 640)
$form.MinimumSize = New-Object System.Drawing.Size(760, 520)
$form.StartPosition = 'CenterScreen'
$form.Font = New-Object System.Drawing.Font('Segoe UI', 9)

$lblStatus = New-Object System.Windows.Forms.Label
$lblStatus.Location = '12,10'; $lblStatus.Size = '860,28'; $lblStatus.Anchor = 'Top,Left,Right'
$lblStatus.Font = New-Object System.Drawing.Font('Segoe UI', 11, [System.Drawing.FontStyle]::Bold)
$form.Controls.Add($lblStatus)

$lblInfo = New-Object System.Windows.Forms.Label
$lblInfo.Location = '12,40'; $lblInfo.Size = '860,18'; $lblInfo.Anchor = 'Top,Left,Right'
$lblInfo.Text = 'Checked programs are allowed to reach the internet. Everything else is blocked.'
$form.Controls.Add($lblInfo)

$list = New-Object System.Windows.Forms.ListView
$list.Location = '12,62'; $list.Size = '720,300'; $list.Anchor = 'Top,Left,Right,Bottom'
$list.View = 'Details'; $list.CheckBoxes = $true; $list.FullRowSelect = $true; $list.GridLines = $true
[void]$list.Columns.Add('Program', 210)
[void]$list.Columns.Add('Path', 420)
[void]$list.Columns.Add('Found', 70)
$form.Controls.Add($list)

function New-Button($text, $y, $handler) {
    $b = New-Object System.Windows.Forms.Button
    $b.Text = $text; $b.Location = "745,$y"; $b.Size = '128,30'; $b.Anchor = 'Top,Right'
    $b.Add_Click($handler)
    $form.Controls.Add($b)
    return $b
}

$opts = New-Object System.Windows.Forms.GroupBox
$opts.Text = 'Options'; $opts.Location = '12,368'; $opts.Size = '720,70'; $opts.Anchor = 'Bottom,Left,Right'
$chkCore = New-Object System.Windows.Forms.CheckBox
$chkCore.Text = 'Keep DNS / DHCP / Windows Core Networking (recommended - otherwise nothing can resolve names)'
$chkCore.Location = '10,20'; $chkCore.Size = '700,20'; $chkCore.Checked = $true
$chkIn = New-Object System.Windows.Forms.CheckBox
$chkIn.Text = 'Block ALL inbound connections, even ones allowed by existing rules'
$chkIn.Location = '10,42'; $chkIn.Size = '700,20'; $chkIn.Checked = $true
$opts.Controls.AddRange(@($chkCore, $chkIn))
$form.Controls.Add($opts)

$log = New-Object System.Windows.Forms.TextBox
$log.Location = '12,445'; $log.Size = '860,145'; $log.Anchor = 'Bottom,Left,Right'
$log.Multiline = $true; $log.ReadOnly = $true; $log.ScrollBars = 'Vertical'
$log.Font = New-Object System.Drawing.Font('Consolas', 9)
$form.Controls.Add($log)

function Log($msg) {
    $log.AppendText(("[{0:HH:mm:ss}] {1}`r`n" -f (Get-Date), $msg))
}

function Add-ListItem($name, $path, $checked) {
    foreach ($it in $list.Items) { if ($it.SubItems[1].Text -eq $path) { return } }
    $item = New-Object System.Windows.Forms.ListViewItem($name)
    [void]$item.SubItems.Add($path)
    [void]$item.SubItems.Add($(if (Test-Path $path) { 'yes' } else { 'MISSING' }))
    $item.Checked = [bool]$checked
    [void]$list.Items.Add($item)
}

function Get-ListApps([switch]$CheckedOnly) {
    $apps = @()
    foreach ($it in $list.Items) {
        if ($CheckedOnly -and -not $it.Checked) { continue }
        $apps += [pscustomobject]@{ Name = $it.Text; Path = $it.SubItems[1].Text; Enabled = $it.Checked }
    }
    return $apps
}

function Save-Config { Write-Json $ConfigPath @(Get-ListApps) }

function Update-Status {
    $state = Get-State
    $profiles = Get-NetFirewallProfile -PolicyStore PersistentStore
    $blocked = @($profiles | Where-Object { "$($_.DefaultOutboundAction)" -eq 'Block' }).Count
    $ours = @(Get-NetFirewallRule -Group $RuleGroup -ErrorAction SilentlyContinue).Count
    if ($state.Active) {
        $lblStatus.Text = "LOCKDOWN ACTIVE  -  outbound blocked on $blocked/3 profiles, $ours allow rule(s)"
        $lblStatus.ForeColor = [System.Drawing.Color]::DarkRed
    } else {
        $lblStatus.Text = 'Lockdown OFF  -  normal Windows firewall behaviour'
        $lblStatus.ForeColor = [System.Drawing.Color]::DarkGreen
    }
}

function Invoke-Safe([scriptblock]$sb) {
    $form.Cursor = [System.Windows.Forms.Cursors]::WaitCursor
    try { & $sb } catch { Log "ERROR: $($_.Exception.Message)" }
    finally { $form.Cursor = [System.Windows.Forms.Cursors]::Default; Update-Status }
}

[void](New-Button 'Auto-detect' 62 {
    Invoke-Safe {
        $c = @(Find-Candidates)
        foreach ($x in $c) { Add-ListItem $x.Name $x.Path $x.Enabled }
        Log "Auto-detect found $($c.Count) program(s)."
        if (-not ($c | Where-Object { $_.Path -like '*\codex.exe' }))    { Log '  Codex not found - use "Add program..."' }
        if (-not ($c | Where-Object { $_.Path -like '*\opencode.exe' })) { Log '  OpenCode not found - use "Add program..."' }
        if (-not ($c | Where-Object { $_.Path -like '*\claude.exe' -or $_.Name -like 'Node*' })) { Log '  Claude Code not found - use "Add program..."' }
    }
})

[void](New-Button 'Add program...' 98 {
    $dlg = New-Object System.Windows.Forms.OpenFileDialog
    $dlg.Filter = 'Programs (*.exe)|*.exe'; $dlg.Multiselect = $true
    if ($dlg.ShowDialog() -eq 'OK') {
        foreach ($f in $dlg.FileNames) { Add-ListItem ([IO.Path]::GetFileNameWithoutExtension($f)) $f $true }
        Save-Config
    }
})

[void](New-Button 'Remove selected' 134 {
    foreach ($it in @($list.SelectedItems)) { $list.Items.Remove($it) }
    Save-Config
})

$btnApply = New-Button 'APPLY LOCKDOWN' 200 {
    $apps = @(Get-ListApps -CheckedOnly)
    if ($apps.Count -eq 0) { [void][System.Windows.Forms.MessageBox]::Show('Check at least one program first.'); return }
    $names = ($apps | ForEach-Object { " - $($_.Name)" }) -join "`n"
    $msg = "Block all network traffic except:`n$names`n`nContinue?"
    if ([System.Windows.Forms.MessageBox]::Show($msg, 'Apply lockdown', 'YesNo', 'Warning') -ne 'Yes') { return }
    Save-Config
    Invoke-Safe { Invoke-Lockdown $apps $chkCore.Checked $chkIn.Checked }
}
$btnApply.BackColor = [System.Drawing.Color]::MistyRose
$btnApply.Font = New-Object System.Drawing.Font('Segoe UI', 9, [System.Drawing.FontStyle]::Bold)

$btnRestore = New-Button 'Restore normal' 236 { Invoke-Safe { Invoke-Restore } }
$btnRestore.BackColor = [System.Drawing.Color]::Honeydew

[void](New-Button 'Import backup...' 300 { Invoke-Safe { Invoke-BackupImport } })
[void](New-Button 'Refresh status' 336 { Update-Status; Log 'Status refreshed.' })

# ---------------------------------------------------------------- startup --
$saved = Read-Json $ConfigPath
if ($saved) {
    foreach ($a in @($saved)) { Add-ListItem $a.Name $a.Path $a.Enabled }
    Log "Loaded $(@($saved).Count) saved program(s) from $ConfigPath"
} else {
    foreach ($x in @(Find-Candidates)) { Add-ListItem $x.Name $x.Path $x.Enabled }
    Log 'First run: auto-detected programs. Review the list, then press APPLY LOCKDOWN.'
}
Update-Status
$form.Add_FormClosing({ try { Save-Config } catch { } })
[void]$form.ShowDialog()
