
<img width="1376" height="768" alt="image_152b9099" src="https://github.com/user-attachments/assets/b6b9b36d-82fa-4114-ae24-d7e2c7997a05" />

---

<img width="1736" height="77" alt="PS2FTPCONNECTOR Multi-ISO Batch Injector and PS2 HDD Manager" src="https://github.com/user-attachments/assets/f8e6acdb-eff2-49e0-b6e4-bdbe70f72670" />

---

<img width="943" height="529" alt="Schermafbeelding 2026-11-15 231737" src="https://github.com/user-attachments/assets/dbcb9a8d-f22f-41ea-9bc6-e7bb6ff238d6" />

---
<img width="1775" height="1102" alt="Schermafbeelding 2026-11-16 000217" src="https://github.com/user-attachments/assets/3cad1dcb-f697-4dd2-a6fd-8e3ea30110ff" />


---

<img width="1718" height="1090" alt="Schermafbeelding 2026-11-15 231856" src="https://github.com/user-attachments/assets/2d0c578a-6936-470f-a690-f93981197474" />

---

<img width="1711" height="1084" alt="Schermafbeelding 2026-11-15 232700" src="https://github.com/user-attachments/assets/f48b8c27-4339-4934-9c2c-f02fc90b3221" />

---

# 🚀 What's New in This Release

## 🔍 Smart Duplicate Comparison & Queue Cleanup
With this new comparison engine, **PS2FTPCONNECTOR** automatically cross-checks your local batch queue against the games already installed on your PlayStation 2 hard drive. Any duplicates found are immediately highlighted with a **glowing red neon border** so you can spot them at a glance—and with the **`OVERWRITE EXISTING GAMES`** toggle unchecked, they are safely skipped automatically during batch transfers.

To keep your queue clean before starting a transfer, two dedicated queue management buttons are built right below the list:
* **`[ - ] REMOVE SELECTED`:** Instantly removes any single selected game from your local batch queue.
* **`[ 🗑 ] REMOVE DUPLICATES`:** Purges all red-bordered duplicate games from your queue in a single click, leaving only fresh titles ready to inject.


## 💾 Automatic Session Logs & "Save Log As..."
To give you complete visibility into every transfer, **PS2FTPCONNECTOR** now automatically saves full session logs to a local `\Logs` folder right next to the executable. Need to inspect an interrupted transfer or troubleshoot a specific ISO? Click **`SAVE LOG AS...`** in the terminal header to export a full transfer report and queue summary anywhere on your PC.

## ⏹ Safe Stop (Zero-Corruption Batch Pause)
Running a massive batch queue and need to shut down for the night? Click **`SAFE STOP`** during an active transfer—the app will finish streaming the current ISO cleanly to completion and then halt the queue safely before starting the next title, ensuring zero corrupted partitions on your PS2 drive.

## 🎨 Sci-Fi Smokey-Glass UI Overhaul
Several parts of the interface received a visual upgrade to match the neon HUD aesthetic—including pulsing hover-glow buttons, rounded sci-fi tabs, file size indicators (`MB` / `GB`) in both lists, and a custom 3D neon-gradient scrollbar.

## ⚡ Tuned TCP/IP Network Throughput
The network streaming engine has been tuned to maximize transfer stability and throughput between your Windows PC and the PlayStation 2 console over TCP/IP.

---


A modern, Smokey-Glass sci-fi C# WPF desktop manager for transferring, backing up, and managing **PlayStation 2 ISO games** over your local network via **HDLGameInstaller** (TCP port `45061`). Built to eliminate tedious command-line transfers with a fast, queue-based batch workflow.

---

<img width="378" height="76" alt="Key Features" src="https://github.com/user-attachments/assets/a4807705-6cce-403b-a66e-5e82f3f5762f" />

* **Multi-ISO Batch Queue & Drag-and-Drop:** Load multiple `.iso` files via file browser or drag-and-drop directly into the queue, with automatic **Game Disc ID** extraction (`SLES` / `SLUS` / `SCES`).
* **Real-Time Telemetry:** Displays live transfer speed (`MB/s`), estimated time remaining (`ETA`), percentage progress, and available PS2 hard drive capacity (`GB`).
* **Installed Games Manager:**
  * Scan all installed games directly from your PS2 internal HDD over the network.
  * Edit game titles on the fly and configure **HDLoader / OPL Compatibility Modes (Mode 1 – Mode 8)**.
  * **Backup to ISO:** Rip installed games from your PS2 hard drive back to `.iso` files on your PC.
  * **Delete Games:** Safely remove unwanted game partitions from the console.
* **Remote Console Power-Off:** Shut down your PlayStation 2 safely from your PC once your batch transfer finishes.

---

<img width="411" height="58" alt="Prerequisites   Setup" src="https://github.com/user-attachments/assets/3b8c30c7-1c04-4441-a518-7d8354559144" />

1. **Operating System:** Windows 10 or Windows 11 (64-bit).
2. **Runtime:** [.NET 8.0 Desktop Runtime](https://microsoft.com).
3. **PlayStation 2 Server:** Launch **`HDLGameInstaller.elf`** on your PlayStation 2 console (ensure your PS2 and PC are connected to the same local network and TCP ports `45061` / `45062` are reachable).
4. **Connect:** Enter your PS2's local IP address in **PS2FTPCONNECTOR** and click **`CONNECT`**.
---
## Setup & Connection Guide (Step-by-Step)

Because **PS2FTPCONNECTOR** communicates directly with **HDLGameInstaller** running on your PlayStation 2 console, you need to load the server `.ELF` on your PS2 first.

### Step 1: Download `HDLGameInstaller.elf` (Official Source)
To ensure you always get the latest 2 TB-compatible build—and to support the original homebrew developers with official download metrics—please download **`HDLGameInstaller.elf`** directly from the official release page:
* **Official GitHub Releases:** [Download HDLGameInstaller from ps2homebrew](https://github.com)

### Step 2: Launch the Server on Your PlayStation 2
1. Copy **`HDLGameInstaller.elf`** to a FAT32 USB flash drive (or directly onto your PS2 internal HDD using *wlauncherELF FILE Browser you can start the HDGAMEINSTALLER.elf from there Keep in mind the Installer works the best trhu mass:/apps/ * HDD0: Gives more of the time trouble but the safest way is thru the mass: <-- this can be your usb stick  ).
2. Connect an Ethernet cable between your PlayStation 2 Network Adapter and your router/switch (on the same local network as your Windows PC).
3. Boot your PlayStation 2 into **wLaunchELF** and launch **`HDLGameInstaller.elf`**. <- This should be on your USB Stick under apps or diffrent map possible )
4. Once loaded, note the **IP Address** displayed on your TV screen (for example: `192.168.0.103`). <-- This is my old connection so yours can be diffrent . Look in the network settings from the HDGameInstaller.elf application adjust the network at your wish but the ip that will show is the right one to hook on.

### Step 3: Connect & Stream from Windows
1. Extract and launch **`PS2FTPCONNECTOR.exe`** on your Windows PC.
2. Enter the **IP Address** shown on your PS2 screen into the **`TARGET_IP`** field.
3. Click **`CONNECT`** (TCP port `45061`). Your installed PS2 games and free HDD space (`GB`) will load automatically.
4. Drag and drop your `.iso` files into the **Batch Upload Queue** and click **`EXECUTE BATCH STREAM`** to start transferring!

---

<img width="753" height="77" alt="Credits and Acknowledgments" src="https://github.com/user-attachments/assets/5e5182ba-be3c-4be0-b2b9-dfdc2a227acb" />

This application acts as a modern Windows frontend and network streaming client for **HDLGameInstaller** on the PlayStation 2. Special thanks and full credit go to the original creator and the homebrew maintainers who made network game installation possible:

* **Original Creator & Protocol Author:** [**sp193**](https://github.com) — creator of `HDLGameInstaller` and its TCP/IP server architecture ([Original Project Archive](https://sites.google.com/view/ysai187/home/projects/hdlgameinstaller)).
* **Community Maintainers:** [**ps2homebrew**](https://github.com/ps2homebrew) — for maintaining and updating the open-source [ps2homebrew/HDLGameInstaller](https://github.com/ps2homebrew/HDLGameInstaller) repository and releases.

---

<img width="231" height="76" alt="License" src="https://github.com/user-attachments/assets/9661c4c3-fbf7-4a6e-aa22-090a5b232772" />

Released under the **GNU General Public License v3.0 (GPL-3.0)** by **R3BU1LD**.
