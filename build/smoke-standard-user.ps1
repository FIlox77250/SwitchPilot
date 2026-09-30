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
        $Process = Start-Process $Exe -ArgumentList '--smoke-test' -WorkingDirectory (Split-Path -Parent $Exe) -Credential $Credential -LoadUserProfile -PassThru
    } else {
        $Process = Start-Process $Exe -ArgumentList '--smoke-test' -PassThru
    }
    Write-Host "Smoke process started: $($Process.Id)"
    if (!$Process.WaitForExit(90000)) { throw 'WPF smoke test timed out' }
    if ($Process.ExitCode -ne 0) { throw "WPF smoke test failed (exit $($Process.ExitCode))" }
} finally {
    if ($Process -and !$Process.HasExited) { $Process.Kill(); $Process.WaitForExit() }
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
