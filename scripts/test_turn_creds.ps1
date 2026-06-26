# test_turn_creds.ps1
# Generates a TURN REST API username:password pair and prints it
# in a few ready-to-use formats for quick manual testing.

$secret = '79d2240e358c22885cac617fb26511548bebec4b'
$ttl    = 86400
$user   = 'alice'
$server = '5.181.23.167'
$port   = 3478

# --- generate ---
$expiry   = [int]([System.DateTime]::UtcNow.Subtract([datetime]'1970-01-01').TotalSeconds) + $ttl
$username = "$expiry`:$user"

$keyBytes = [System.Text.Encoding]::UTF8.GetBytes($secret)
$h        = New-Object System.Security.Cryptography.HMACSHA1($keyBytes)
$msgBytes = [System.Text.Encoding]::UTF8.GetBytes($username)
$hash     = $h.ComputeHash($msgBytes)
$password = [Convert]::ToBase64String($hash)
$h.Dispose()

# --- output ---
Write-Output "=== TURN credentials ==="
Write-Output "username : $username"
Write-Output "password : $password"
Write-Output ""
Write-Output "=== iceServers JSON (for app/browser) ==="
Write-Output @"
{
  "urls": "turn:$server`:$port?transport=tcp",
  "username": "$username",
  "credential": "$password"
}
"@
Write-Output ""
Write-Output "=== turnutils_uclient test command ==="
Write-Output "turnutils_uclient -t -u $username -w $password $server"
