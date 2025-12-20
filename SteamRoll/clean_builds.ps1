# clean_builds.ps1
# Script to clean up all build and publish directories

Write-Host "Stopping any running SteamRoll instances..."
Stop-Process -Name "SteamRoll" -Force -ErrorAction SilentlyContinue

# List of directories to remove
$directories = @(
    "publish",
    "publish2",
    "publish_test",
    "output",
    "output2",
    "bin",
    "obj"
)

foreach ($dir in $directories) {
    if (Test-Path $dir) {
        Write-Host "Removing '$dir'..."
        Remove-Item -Path $dir -Recurse -Force -ErrorAction SilentlyContinue
        
        if (Test-Path $dir) {
            Write-Host "  Warning: Could not fully remove '$dir'. Some files may be locked." -ForegroundColor Yellow
        } else {
            Write-Host "  Success." -ForegroundColor Green
        }
    } else {
        Write-Host "'$dir' does not exist, skipping." -ForegroundColor Gray
    }
}

Write-Host "`nCleanup complete. You can now rebuild with a fresh output directory." -ForegroundColor Cyan
