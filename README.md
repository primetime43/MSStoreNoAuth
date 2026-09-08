# MSStoreNoAuth

> Install Microsoft Store apps without signing in to a Microsoft account

---

## Overview

**MSStoreNoAuth** is a simple CLI tool that lets you install apps from the Microsoft Store by URL or Store ID, without forcing an interactive Microsoft account login. Under the hood it uses `winget` (msstore source), and offers:

- **Interactive mode** (paste URL or ID when prompted)
- **Argument mode** (pass URL or ID on the command-line)
- **Auto-accept** or **manual** agreement confirmation
- **Error-code mapping** for friendlier messages
- **Targeted fallback** to manual mode when winget specifically requires interaction
- **Live winget output** so longer Store installs do not appear stuck
- **Already-installed detection** instead of reporting "no applicable update" as a failure
- **Exact Store ID matching** to prevent ambiguous package selection
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

Pass a Store URL or raw Store ID. Argument mode defaults to auto-accept and exits when the install finishes:

```
MSStoreNoAuth.exe https://apps.microsoft.com/detail/xp89dcgq3k6vld
```

or

```
MSStoreNoAuth.exe xp89dcgq3k6vld
```

Choose manual mode explicitly if you want to answer winget's package prompts:

```
MSStoreNoAuth.exe --manual https://apps.microsoft.com/detail/9pl7plm5bft2
```

### Interactive mode

Run without arguments and paste the URL or Store ID when prompted:

```
MSStoreNoAuth.exe
```

You'll then be asked to choose between auto-accept or manual mode for agreement confirmation. After each install, you can choose to install another app without restarting.

![image](https://github.com/user-attachments/assets/f9222b20-fa76-49ec-8c80-362da89cb21e)

---

## Creating a release

Commit the desired release contents, create a version tag on that commit, and push the tag:

```powershell
git tag v1.2
git push origin v1.2
```

The GitHub Actions workflow builds a self-contained Windows x64 `.exe` and attaches it to a GitHub Release with generated release notes. Numeric tags such as `1.2` are also supported.
