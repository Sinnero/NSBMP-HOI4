# NSBMP Pacer: Adaptive Speed 5 Host Companion
**Real-Time Dynamic Multiplayer Speed Synchronization for Hearts of Iron IV**  
**Created by Sinnero**

[![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011%20(64--bit)-blue.svg)](#)
[![Zero Dependencies](https://img.shields.io/badge/Dependencies-Zero%20(Built--in%20.NET)-success.svg)](#)
[![License](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)
[![Author](https://img.shields.io/badge/Author-Sinnero-orange.svg)](#)

---

## Overview

In Hearts of Iron IV multiplayer, when a host with a high-end CPU plays on **Speed 5**, slower client CPUs cannot keep up. This causes command drops (`COMMAND LOST!`), desyncs, and triggers the game's broken auto-downshift that permanently traps the lobby at Speed 1.

**NSBMP Pacer** is a lightweight, zero-dependency Windows companion tool that reads client telemetry in real time and dynamically paces Speed 5 to match the slowest player's maximum CPU capacity—giving you the fastest possible game without lag spikes or desyncs.

---

## Key Features

* **Zero Dependencies**: Native Windows 64-bit app. No Python, Java, or runtimes required.
* **1-Click Auto-Run**: Starts quietly with Windows in your system tray (0% CPU) and automatically attaches whenever HOI4 launches (Steam, Paradox Launcher, or shortcut).
* **Adaptive Ping-Floor & Pacing**: Distinguishes network ping from CPU lag, smoothly pacing ticks and absorbing midnight calculation spikes.
* **Anti-Desync Protection**: Prevents command drops, eliminates lag multiplier bugs, and stops the 4-hour Speed 1 death spiral.
* **100% Safe**: Zero DLL injections, zero hooks, and zero antivirus false positives.

---

## Quick Start

1. Download and run [**`NSBMP_Pacer.exe`**](NSBMP_Pacer.exe).
2. Click **`Enable Auto-Run`**.
3. Launch Hearts of Iron IV and host your multiplayer game on **Speed 5**!

> **Note:** Only the lobby host needs to run `NSBMP_Pacer.exe`. Clients and other players do not need to install anything.

---

## Dashboard Controls

* **Enable / Disable Auto-Pacing**: Toggle adaptive pacing on or off at any time.
* **Reset Speed 5 (0.0s)**: Instantly restores unthrottled singleplayer speed.
* **Minimize to Tray**: Runs silently next to your Windows clock using 0% CPU and ~12 MB RAM.

---

## Building from Source

Every Windows 10/11 PC includes Microsoft's official C# compiler. You can inspect the source in [`src/`](src/) and compile it in 2 seconds:
1. Double-click **`build.bat`**.
2. It compiles standalone `NSBMP_Pacer.exe` using built-in `csc.exe`.

---

## Credits & License

Created by **Sinnero**.  
Licensed under the [MIT License](LICENSE).
