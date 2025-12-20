# 🎮 SteamRoll

![SteamRoll Logo](SteamRoll/logo.png)


**SteamRoll** is a tool designed for households to easily share and play their Steam libraries across local computers without the restrictions of Steam Family Sharing or requiring multiple accounts to be online.

It creates portable, LAN-ready game packages by automatically applying compatibility layers (Goldberg Emulator / CreamAPI) and provides a high-speed peer-to-peer transfer system to move games between PCs.

## ✨ Features

*   **📦 One-Click Packaging**: Automatically converts installed Steam games into portable, DRM-free folders.
*   **🔓 Auto-Configuration**: 
    *   Automatically downloads and applies **Goldberg Emulator** to bypass Steam DRM for LAN play.
    *   Optional **CreamAPI** support for unlocking DLC content.
    *   Generates custom `LAUNCH.bat` scripts for easy one-click play.
    *   Special handling for Source Engine games (e.g., Half-Life 2 mods).
*   **🚀 Direct LAN Transfer**:
    *   Built-in high-speed TCP file transfer between SteamRoll clients.
    *   No external drives or slow cloud downloads required.
    *   **Integrity Verification**: Uses SHA-256 hashing to ensure game files are not corrupted during transfer.
*   **🛡️ Robust & Safe**:
    *   Verifies file hashes after transfer.
    *   Isolated environment (does not modify your actual Steam installation).
    *   "Offline" mode configuration to prevent accidental Steam connection attempts.

## 🚀 How to Use

### 1. Preparation
Run **SteamRoll** on the computer where the games are installed. It will automatically detect your Steam library.

### 2. Create a Package
1.  Select a game from the list.
2.  Click the **Create Package** button.
3.  SteamRoll will:
    *   Copy the game files to a staging area.
    *   Detect DRM protection (SteamStub/Denuvo). *Note: Denuvo games are not supported.*
    *   Apply the appropriate emulator (Goldberg/CreamAPI).
    *   Generate a launcher and metadata.

### 3. Transfer to Another PC
**Option A: Direct Transfer (Recommended)**
1.  Open **SteamRoll** on the destination PC. It will automatically start listening for peers.
2.  On the source PC, right-click the packaged game and select **Send to Peer**.
3.  Select the destination PC from the list.
4.  Accept the transfer on the destination PC.

**Option B: Manual Copy**
1.  Click **Open Output / Open Package** to view the folder.
2.  Copy the entire game folder to a USB drive or network share.
3.  Paste it onto the target PC.

### 4. Play!
On the target PC, simply open the game folder and run **`LAUNCH.bat`**. No Steam login required!

## System Requirements
*   **OS**: Windows 10/11 (64-bit)
*   **Runtime**: .NET 8.0 Desktop Runtime
*   **Steam**: Installed on the source machine (not required on target).

## ⚠️ Disclaimer
**SteamRoll is for personal, local use only.**
Please respect game developers. Only use this tool to play games you legally own on your own local network. Do not use this tool to distribute pirated copies of games.
