param([string]$Exe = 'artifacts/win-x64/SwitchPilot.exe')
$ErrorActionPreference = 'Stop'
$Exe = (Resolve-Path $Exe).Path
$Output = Join-Path (Split-Path -Parent (Split-Path -Parent $Exe)) 'screens'
$Admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (!$Admin) {
    $Process = Start-Process $Exe -ArgumentList '--smoke-test' -PassThru
    if (!$Process.WaitForExit(90000)) { $Process.Kill(); throw 'WPF smoke test timed out' }
    if ($Process.ExitCode -ne 0) { throw 'WPF smoke test failed' }
    Copy-Item "$env:APPDATA/SwitchPilot/SmokeTest" $Output -Recurse -Force
    exit 0
}
# Hosted Windows runners are elevated. Run the application under a temporary standard
# account instead of weakening its asInvoker / non-administrator launch guard.
$User = 'sp-ci-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$Task = 'SwitchPilot-Smoke-' + [Guid]::NewGuid().ToString('N')
$Password = [Guid]::NewGuid().ToString('N') + 'aA1!'
try {
    $Account = New-LocalUser -Name $User -Password (ConvertTo-SecureString $Password -AsPlainText -Force) -AccountNeverExpires
    $Users = Get-LocalGroup -SID 'S-1-5-32-545'
    Add-LocalGroupMember -Group $Users -Member $Account
    $Action = New-ScheduledTaskAction -Execute $Exe -Argument '--smoke-test' -WorkingDirectory (Split-Path -Parent $Exe)
    $Settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 2)
    Register-ScheduledTask -TaskName $Task -Action $Action -Settings $Settings -User "$env:COMPUTERNAME\$User" -Password $Password -RunLevel Limited | Out-Null
    Start-ScheduledTask -TaskName $Task
    $Deadline = (Get-Date).AddSeconds(100)
    do {
        Start-Sleep -Milliseconds 500
        $Info = Get-ScheduledTaskInfo -TaskName $Task
        $Running = (Get-ScheduledTask -TaskName $Task).State -eq 'Running'
    } while (($Running -or $Info.LastRunTime.Year -lt 2020) -and (Get-Date) -lt $Deadline)
    if ($Running -or $Info.LastRunTime.Year -lt 2020) { throw 'Standard-user smoke test timed out' }
    $Profile = Get-CimInstance Win32_UserProfile | Where-Object SID -eq $Account.SID.Value
    if (!$Profile) { throw 'Standard-user profile not created' }
    $Result = Join-Path $Profile.LocalPath 'AppData/Roaming/SwitchPilot/SmokeTest'
    if (Test-Path $Result) { Copy-Item $Result $Output -Recurse -Force }
    if ($Info.LastTaskResult -ne 0 -or !(Test-Path (Join-Path $Result 'result.txt'))) { throw 'Standard-user WPF smoke test failed' }
    if (!(Get-Content (Join-Path $Result 'result.txt') -Raw).StartsWith('PASS:')) { throw 'Smoke test did not report success' }
} finally {
    if (Get-ScheduledTask -TaskName $Task -ErrorAction SilentlyContinue) {
        Stop-ScheduledTask -TaskName $Task -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskName $Task -Confirm:$false
    }
    if (Get-LocalUser -Name $User -ErrorAction SilentlyContinue) { Remove-LocalUser -Name $User }
    $Password = $null
}
