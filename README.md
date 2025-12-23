# 🎮 SteamRoll

![SteamRoll Logo](SteamRoll/logo.png)

**SteamRoll** is a tool designed for households to easily share and play their Steam libraries across local computers without the restrictions of Steam Family Sharing or requiring multiple accounts to be online.

It creates portable, LAN-ready game packages by automatically applying compatibility layers (Goldberg Emulator / CreamAPI) and provides a high-speed peer-to-peer transfer system to move games between PCs.

## ✨ Features

### 📦 Automated Packaging
*   **One-Click Conversion**: Automatically converts installed Steam games into portable, DRM-free folders.
*   **Smart Emulation**:
    *   **Goldberg Emulator**: Automatically downloads and applies the Goldberg Emulator to bypass Steam DRM for offline/LAN play.
    *   **CreamAPI Support**: Optional support for unlocking DLC content while maintaining Steam integration.
    *   **Interface Detection**: Scans game files to detect and configure necessary Steam interfaces.
*   **Game-Specific Logic**:
    *   **Source Engine Support**: Special handling for Source engine games (e.g., Half-Life 2 mods) to correctly configure `gameinfo.txt` and launch arguments.
    *   **Launcher Generation**:
        *   **Windows**: Creates custom `LAUNCH.bat` scripts for easy one-click play.
        *   **Linux / Steam Deck**: Generates `launch.sh` scripts that handle Wine detection, path conversion, and `LD_LIBRARY_PATH` configuration.
    *   **Dependency Scripting**: Automatically detects redistributables (DirectX, VC++, PhysX) and generates an `install_deps.bat` for silent installation.
*   **Rich Metadata**: Fetches game details (descriptions, release dates, ratings) from the Steam Store and generates a detailed `README.txt` and machine-readable `steamroll.json` for every package.
*   **Resumable Packaging**: If a packaging operation is interrupted, SteamRoll can detect the partial state and resume from the last completed step (Copying, Interface Detection, etc.).

### 🚀 Advanced LAN Transfer
*   **Zero-Config Discovery**: Automatically finds other SteamRoll clients on your local network via UDP broadcast (Port 27050).
*   **Smart Sync (Differential Transfer)**: Uses a file-level transfer protocol to analyze the destination folder before sending. It intelligently skips files that match in size and hash, making it perfect for resuming interrupted transfers or pushing small game updates without re-sending the whole game.
*   **Resumable Transfers**: Transfers can be paused or interrupted and resumed later. State is tracked via `.steamroll_transfer_state` to ensure seamless continuation.
*   **Smart Hashing**: Intelligently uses existing package metadata to skip re-hashing unchanged files on the source, ensuring instant transfer initialization for large games.
*   **Compression**: Optional GZip compression (negotiated via `STEAMROLL_TRANSFER_V2` protocol) to reduce bandwidth usage during transfers.
*   **Integrity Verification**: Uses SHA-256 hashing to ensure every file is transferred correctly and matches the source.
*   **Remote Library Browsing**: Browse the library of other SteamRoll peers on your network and request "Pull" transfers directly from their machine.
*   **Repair from Peer**: Verify local game files against a peer's copy and download only the missing or corrupt files to repair a broken package.
*   **Network Speed Test**: Built-in tool to measure raw transfer throughput between peers.
*   **Bandwidth Control**: Configurable transfer speed limits to prevent network saturation.

### 🌐 Global Mesh Library
*   **Unified Game View**: See all games available across your entire LAN in one unified library view.
*   **Network Availability Badges**: Games available from peers display a "📡 Network Available" badge.
*   **One-Click Install from Peer**: Install games directly from any peer on your network with a single click.
*   **Automatic Game List Sharing**: Peers automatically share their game lists when connecting to the network.

### ⚡ Batch Operations
*   **Batch Packaging**: Select multiple games in the library and package them sequentially in one go.
*   **Batch Transfer**: Select multiple packaged games and send them all to a peer in a single operation.

### 🛠️ Management & Safety
*   **Library Scanning**: Automatically detects games across multiple Steam library folders with caching for performance.
*   **Health Checks & Diagnostics**:
    *   **Diagnostics**: Analyzes packages for issues like architecture mismatches (32-bit vs 64-bit DLLs), missing Steam API files, or junk files (Redistributables).
    *   **Library Cleanup**: Scans for and removes orphaned files that don't belong to any valid package to reclaim disk space.
*   **Save Game Synchronization**:
    *   **Backup & Restore**: Built-in tools to back up and restore game saves to/from ZIP files.
    *   **P2P Sync**: Directly synchronize save games with a peer over the network without intermediate file steps.
    *   **Manual Sync Button**: One-click "Sync Saves" button in game details to sync saves on demand.
    *   **Automatic Background Sync**: Optional setting to automatically sync saves in the background.
    *   **Versioned Backups**: Automatically maintains up to 5 versioned backups of your saves.
    *   **Conflict Detection**: Smart detection of save conflicts with resolution options.
*   **Package Import**: Easily ingest external SteamRoll packages (ZIPs) via drag-and-drop or the Import button.
*   **Update System**: Checks for updates to both the SteamRoll application and the Goldberg Emulator (supports GitHub fork for newer Steam SDKs).

### 🔧 Health & Dependency "Magic Fixer"
*   **Automatic Dependency Detection**: Scans game folders to detect required runtimes (VC++ 2008-2022, DirectX, PhysX).
*   **One-Click Repair**: "Repair" button in game details automatically downloads and installs missing dependencies.
*   **Registry Verification**: Checks Windows registry to verify if dependencies are already installed.
*   **Silent Installation**: Downloads official Microsoft redistributables and installs them silently.
*   **Cached Downloads**: Downloaded installers are cached locally to speed up future repairs.

### 🔒 Security
*   **Defender Exclusion Helper**: An optional utility to safely add Windows Defender exclusions for SteamRoll folders to prevent false positives.
*   **Path Validation**: Strict security checks during transfers to prevent directory traversal attacks.
*   **Isolated Environment**: SteamRoll works in its own output directory and does **not** modify your actual Steam installation.

## 🚀 How to Use

### 1. Preparation
Run **SteamRoll** on the computer where the games are installed. It will automatically detect your Steam library.

### 2. Create a Package
1.  Select a game from the "Library" view.
2.  Click the **Create Package** button.
3.  SteamRoll will:
    *   Copy game files to the staging area.
    *   Apply the selected emulator (Goldberg/CreamAPI).
    *   Configure DLC and generate launchers for Windows and Linux.
    *   Create metadata and dependency installers.

### 3. Transfer to Another PC
**Option A: Direct LAN Transfer (Recommended)**
1.  Open **SteamRoll** on the destination PC. It will automatically start listening.
2.  On the source PC, go to the "Packages" view.
3.  Right-click the packaged game and select **Send to Peer**.
4.  Select the destination PC from the list.
5.  On the destination PC, accept the incoming transfer request.

**Option B: Manual Copy**
1.  Click **Open Folder** to view the package.
2.  Copy the folder to a USB drive or network share.
3.  Paste it onto the target PC.

### 4. Play!
On the target PC, open the game folder:
*   **Windows**: Run **`LAUNCH.bat`**.
*   **Linux / Steam Deck**: Run **`launch.sh`** (ensure Wine is installed if running Windows executables).

## ⌨️ Keyboard Shortcuts

*   **Ctrl + F**: Focus Search Box
*   **Ctrl + R**: Refresh Library
*   **Ctrl + S**: Open Settings
*   **Esc**: Cancel current operation

## ⚙️ Technical Details

*   **Ports**:
    *   **TCP 27051**: File transfer
    *   **UDP 27050**: Peer discovery
*   **Configuration**: Settings are stored in `%LocalAppData%/SteamRoll/settings.json`.
*   **Goldberg Path**: Emulator files are managed in `%LocalAppData%/SteamRoll/Goldberg`.
*   **Metadata**: Each package contains a `steamroll.json` file with build info, emulator version, and SHA-256 file hashes for integrity checks.
*   **Dependencies**: The generated `install_deps.bat` script can silently install common redistributables found in the game folder (e.g., `_CommonRedist`).

## System Requirements
*   **OS**: Windows 10/11 (64-bit)
*   **Runtime**: .NET 8.0 Desktop Runtime
*   **Steam**: Installed on the source machine (not required on target).

## ⚠️ Disclaimer
**SteamRoll is for personal, local use only.**
Please respect game developers. Only use this tool to play games you legally own on your own local network. Do not use this tool to distribute pirated copies of games.
