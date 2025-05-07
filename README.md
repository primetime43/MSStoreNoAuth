# MSStoreNoAuth

> Install Microsoft Store apps without signing in to a Microsoft account

---

## 🚀 Overview

**MSStoreNoAuth** is a simple CLI tool that lets you install apps from the Microsoft Store by URL or Store ID, without forcing an interactive Microsoft account login. Under the hood it uses `winget` (msstore source), and offers:

- **Interactive mode** (paste URL or ID when prompted)  
- **Argument mode** (pass URL or ID on the command-line)  
- **Auto-accept** or **manual** agreement confirmation  
- **Error-code mapping** for friendlier messages  
- **Automatic fallback** to manual mode if auto-accept fails  
- **Loop support** so you can install multiple apps in one session  

---

## 🔧 Prerequisites

- **Windows 10/11** (with the Microsoft Store installed)  
- **Windows Package Manager** (`winget`)  
- **.NET 8 Runtime** (or later)  

---

## 📥 Installation

1. Download the latest `MSStoreNoAuth.exe` from the [Releases](https://github.com/you/MSStoreNoAuth/releases) page  

---

## ⚙️ Usage

### Argument mode

Pass a Store URL or raw Store ID as the only argument

![image](https://github.com/user-attachments/assets/f9222b20-fa76-49ec-8c80-362da89cb21e)
