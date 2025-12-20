# build_single_file.ps1
# Automates the creation of the single-file self-contained executable

Write-Host "Build Process Started..." -ForegroundColor Cyan

# 1. Clean previous builds
Write-Host "Cleaning previous builds..."
if (Test-Path ".\clean_builds.ps1") {
    .\clean_builds.ps1
}
else {
    dotnet clean
    if (Test-Path "bin") { Remove-Item -Path "bin" -Recurse -Force -ErrorAction SilentlyContinue }
    if (Test-Path "obj") { Remove-Item -Path "obj" -Recurse -Force -ErrorAction SilentlyContinue }
}

# 2. Publish Single File
Write-Host "Publishing Single-File Executable..." -ForegroundColor Cyan
$publishCommand = "dotnet publish SteamRoll.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true"
Write-Host "Executing: $publishCommand"

Invoke-Expression $publishCommand

if ($LASTEXITCODE -eq 0) {
    $exePath = "bin\Release\net8.0-windows\win-x64\publish\SteamRoll.exe"
    if (Test-Path $exePath) {
        Write-Host "`nBuild SUCCESS!" -ForegroundColor Green
        Write-Host "Executable created at:" -ForegroundColor White
        Write-Host (Resolve-Path $exePath) -ForegroundColor Yellow
        
        # Optional: Open the folder
        # Invoke-Item (Split-Path (Resolve-Path $exePath))
    }
    else {
        Write-Host "`nBuild seemed to succeed but executable not found at expected path: $exePath" -ForegroundColor Red
    }
}
else {
    Write-Host "`nBuild FAILED!" -ForegroundColor Red
}
