# AllowList Firewall (Windows 10)

A small GUI on top of the built-in Windows Defender Firewall that **blocks all
network traffic except the programs you allow**. By default it looks for:

- Google Chrome
- Claude Code (`~\.local\bin\claude.exe`), plus the Claude desktop app if it's installed
- OpenAI Codex CLI (`codex.exe`, including the native binary inside the npm package)
- OpenCode (`opencode.exe` from npm, scoop or the installer)

There's nothing to install. It's a single PowerShell script that uses WinForms and
the standard `NetSecurity` cmdlets that come with Windows 10.

## Usage

1. Copy this folder to the Windows machine.
2. Double-click `Run-AllowListFirewall.bat` and accept the UAC prompt.
3. Look over the detected list. Use **Add program...** for anything it missed, and untick or remove anything you don't want.
4. Click **APPLY LOCKDOWN**.
5. To undo it, click **Restore normal**.

## What "Apply lockdown" does

1. The first time, it exports the full firewall configuration to
   `C:\ProgramData\AllowListFirewall\backup-*.wfw` and records each profile's settings.
2. It disables every other enabled **outbound allow** rule, because any of them would
   let traffic through. It records which ones it disabled so that **Restore** turns
   only those back on.
3. It creates one outbound allow rule per checked program in the rule group
   `AllowList Firewall`.
4. It sets the Domain, Private and Public profiles to *Enabled* with
   *Outbound: Block* and *Inbound: Block*. If you leave the option ticked, it also
   ignores existing inbound allow rules.

You can press Apply again at any time, for example after an app update moves its
`.exe`. The program rules are rebuilt, and any new allow rules Windows added are
disabled.

**Import backup...** is the emergency option. It restores a `.wfw` snapshot
completely with `netsh advfirewall import`.

## Things to know

- **Keep the "DNS / DHCP / Core Networking" option ticked.** If you untick it, the
  allowed apps can't resolve host names either.
- **Everything else goes offline**, including Windows Update, Defender signature
  updates, the Microsoft Store, Chrome's updater (`GoogleUpdater.exe`), `git.exe`,
  `ssh.exe`, `curl.exe`, and so on. If you push with git from Claude Code, Codex or
  OpenCode, add `git.exe` (and `ssh.exe` if you use SSH remotes) with **Add program...**.
- **Claude Code installed via npm** runs as `node.exe`. Auto-detect adds Node.js
  unticked, and ticks it only when it finds no native `claude.exe`. Allowing
  `node.exe` lets *any* Node script reach the internet. The native Claude Code
  installer avoids this.
- **The Claude desktop app** installs into versioned `app-x.y.z` folders. After it
  updates, press **Auto-detect** and then **APPLY LOCKDOWN** again.
- Rules pushed by **Group Policy** (corporate machines) can't be disabled from here
  and still apply.
- Tools running **inside WSL2 or VMs** don't go through these per-program rules.
