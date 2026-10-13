# A focused regression check for a credentialed test process inheriting the
# runner administrator's profile variables. It changes only this process.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'Initialize-TestUserEnvironment.ps1')
$profileNames = @('USERPROFILE', 'LOCALAPPDATA', 'APPDATA', 'HOMEDRIVE', 'HOMEPATH', 'USERNAME', 'USERDOMAIN', 'TEMP', 'TMP')
$preservedNames = @('PATH', 'DOTNET_ROOT', 'GITHUB_ACTIONS', 'RUNNER_ENVIRONMENT')
$original = @{}
foreach ($name in $profileNames + $preservedNames) { $original[$name] = [Environment]::GetEnvironmentVariable($name) }
try {
    Initialize-TestUserEnvironment
    $expected = @{}
    foreach ($name in $profileNames) { $expected[$name] = [Environment]::GetEnvironmentVariable($name) }
    foreach ($name in $profileNames) { [Environment]::SetEnvironmentVariable($name, 'C:\Users\runneradmin\Inherited-' + $name) }
    Initialize-TestUserEnvironment
    foreach ($name in $profileNames) {
        if ([Environment]::GetEnvironmentVariable($name) -ne $expected[$name]) { throw "The loaded user's $name was not restored." }
    }
    foreach ($name in $preservedNames) {
        if ([Environment]::GetEnvironmentVariable($name) -ne $original[$name]) { throw "The CI variable $name changed." }
    }
    if ([Environment]::GetFolderPath('UserProfile') -ne $expected['USERPROFILE'] -or
        [Environment]::GetFolderPath('LocalApplicationData') -ne $expected['LOCALAPPDATA'] -or
        [Environment]::GetFolderPath('ApplicationData') -ne $expected['APPDATA']) {
        throw 'Known-folder lookups still use the inherited profile.'
    }
    Write-Output 'Verified loaded-user profile restoration and inherited CI/tool paths.'
}
finally {
    foreach ($name in $profileNames + $preservedNames) { [Environment]::SetEnvironmentVariable($name, $original[$name]) }
}
