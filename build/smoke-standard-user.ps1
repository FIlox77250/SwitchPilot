param([string]$Exe = 'artifacts/win-x64/SwitchPilot.exe')
$ErrorActionPreference = 'Stop'
$Exe = (Resolve-Path $Exe).Path
$Output = Join-Path (Split-Path -Parent (Split-Path -Parent $Exe)) 'screens'
$Admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$Account = $null
$Process = $null
$Password = $null
try {
    if ($Admin) {
        # Use a standard account without depending on the runner's Task Scheduler.
        # Start-Process -Credential -LoadUserProfile loads its profile for DPAPI/WPF.
        $User = 'sp-ci-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
        $Password = ConvertTo-SecureString ([Guid]::NewGuid().ToString('N') + 'aA1!') -AsPlainText -Force
        $Account = New-LocalUser -Name $User -Password $Password -AccountNeverExpires
        Add-LocalGroupMember -Group (Get-LocalGroup -SID 'S-1-5-32-545') -Member $Account
        $Credential = [PSCredential]::new("$env:COMPUTERNAME\$User", $Password)
        # Alternate-credential processes inherit the runner's environment. Restore
        # this account's folders before .NET extracts the single-file native runtime.
        $QuotedExe = $Exe.Replace("'", "''")
        $Bootstrap = @'
$Sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$Profile = (Get-ItemProperty -LiteralPath ("HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\" + $Sid)).ProfileImagePath
$env:USERPROFILE = $Profile
$env:APPDATA = Join-Path $Profile 'AppData\Roaming'
$env:LOCALAPPDATA = Join-Path $Profile 'AppData\Local'
$env:TEMP = Join-Path $env:LOCALAPPDATA 'Temp'
$env:TMP = $env:TEMP
New-Item -ItemType Directory -Path $env:TEMP -Force | Out-Null
$Child = Start-Process -FilePath '__EXE__' -ArgumentList '--smoke-test' -PassThru
$Child.WaitForExit()
exit $Child.ExitCode
'@
        $Bootstrap = $Bootstrap.Replace('__EXE__', $QuotedExe)
        $Encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($Bootstrap))
        $PowerShell = Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
        $Process = Start-Process $PowerShell -ArgumentList '-NoProfile', '-NonInteractive', '-EncodedCommand', $Encoded -WorkingDirectory (Split-Path -Parent $Exe) -Credential $Credential -LoadUserProfile -PassThru
    } else {
        $Process = Start-Process $Exe -ArgumentList '--smoke-test' -PassThru
    }
    Write-Host "Smoke process started: $($Process.Id)"
    if (!$Process.WaitForExit(90000)) { throw 'WPF smoke test timed out' }
    if ($Process.ExitCode -ne 0) { throw "WPF smoke test failed (exit $($Process.ExitCode))" }
} finally {
    if ($Process -and !$Process.HasExited) { $Process.Kill($true); $Process.WaitForExit() }
    if ($Account) {
        $Profile = Get-CimInstance Win32_UserProfile | Where-Object SID -eq $Account.SID.Value
        $Result = if ($Profile) { Join-Path $Profile.LocalPath 'AppData/Roaming/SwitchPilot/SmokeTest' } else { $null }
    } else {
        $Result = Join-Path $env:APPDATA 'SwitchPilot/SmokeTest'
    }
    if ($Result -and (Test-Path $Result)) {
        New-Item -ItemType Directory -Path $Output -Force | Out-Null
        Copy-Item "$Result/*" $Output -Recurse -Force
        Get-ChildItem $Result -Filter '*.txt' | ForEach-Object { Get-Content $_.FullName }
    }
    if ($Account) { Remove-LocalUser -Name $Account.Name }
    $Password = $null
    $Credential = $null
}
$Report = Join-Path $Output 'result.txt'
if (!(Test-Path $Report) -or !(Get-Content $Report -Raw).StartsWith('PASS:')) { throw 'Smoke test did not report success' }
