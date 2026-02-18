# MSStoreNoAuth

> Install Microsoft Store apps without signing in to a Microsoft account

---

## Overview

**MSStoreNoAuth** is a simple CLI tool that lets you install apps from the Microsoft Store by URL or Store ID, without forcing an interactive Microsoft account login. Under the hood it uses `winget` (msstore source), and offers:

- **Interactive mode** (paste URL or ID when prompted)
- **Argument mode** (pass URL or ID on the command-line)
- **Auto-accept** or **manual** agreement confirmation
- **Error-code mapping** for friendlier messages
- **Automatic fallback** to manual mode if auto-accept fails
- **Loop support** so you can install multiple apps in one session

---

## Prerequisites

- **Windows 10/11** (with the Microsoft Store installed)
- **Windows Package Manager** (`winget`)

> **Note:** No .NET runtime installation is required. The app is published as a self-contained single-file executable with the runtime bundled in.

---

## Installation

1. Download the latest `MSStoreNoAuth.exe` from the [Releases](https://github.com/primetime43/MSStoreNoAuth/releases) page
2. Run it — no installation or additional dependencies needed

---

## Usage

### Argument mode

Pass a Store URL or raw Store ID as the only argument:

```
MSStoreNoAuth.exe https://apps.microsoft.com/detail/xp89dcgq3k6vld
```

or

```
MSStoreNoAuth.exe xp89dcgq3k6vld
```

### Interactive mode

Run without arguments and paste the URL or Store ID when prompted:

```
MSStoreNoAuth.exe
```

You'll then be asked to choose between auto-accept or manual mode for agreement confirmation. After each install, you can choose to install another app without restarting.

![image](https://github.com/user-attachments/assets/f9222b20-fa76-49ec-8c80-362da89cb21e)

---

The workflow builds a self-contained single-file `.exe` and attaches it to a GitHub Release with auto-generated release notes.
