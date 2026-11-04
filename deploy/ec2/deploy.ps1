# One-command deploy from your Windows PC to the EC2 server.
# It: packs the code -> copies it to EC2 -> builds -> migrates -> starts -> checks /health.
# The server must already have Docker and the Postgres container (set up by the Notification deploy).
#
# Usage (PowerShell, from anywhere):
#   .\deploy\ec2\deploy.ps1
#   .\deploy\ec2\deploy.ps1 -HostIp 3.111.217.38      # when the EC2 public IP changed
param(
    [string]$HostIp = "3.111.217.38",
    [string]$KeyPath = "$HOME\Downloads\routego-dev-key.pem",
    [string]$User = "ec2-user"
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$archive = Join-Path $env:TEMP "identity.tar.gz"
$target = "$User@$HostIp"

function Assert-LastExit([string]$step) {
    if ($LASTEXITCODE -ne 0) { throw "FAILED at step: $step (exit code $LASTEXITCODE)" }
}

if (-not (Test-Path $KeyPath)) { throw "Key file not found: $KeyPath  (pass -KeyPath <path to .pem>)" }

Write-Host "1/3 Packing code from $repoRoot" -ForegroundColor Cyan
tar.exe -czf $archive --exclude=bin --exclude=obj --exclude=.git --exclude=.vs --exclude=.env -C $repoRoot .
Assert-LastExit "pack code"

Write-Host "2/3 Copying code to $target" -ForegroundColor Cyan
scp -i $KeyPath -o StrictHostKeyChecking=accept-new $archive "${target}:~/identity.tar.gz"
Assert-LastExit "copy code"

Write-Host "3/3 Unpacking, building and starting on the server" -ForegroundColor Cyan
$remote = "rm -rf ~/identity && mkdir -p ~/identity" +
    " && tar -xzf ~/identity.tar.gz -C ~/identity" +
    " && sudo bash ~/identity/deploy/ec2/start.sh"
ssh -i $KeyPath $target $remote
Assert-LastExit "build and start"

Write-Host "DONE. Test: http://${HostIp}:5001/health (needs port 5001 open for My IP)" -ForegroundColor Green
