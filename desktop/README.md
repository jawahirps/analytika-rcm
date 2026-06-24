# Bix Desktop Installers

Native installers that run Bix as a local desktop app. The app is the same
ASP.NET Core web app — the desktop build starts a local web server on
`http://localhost:5097` and opens your default browser. Data (SQLite DB +
encrypted-credential keys) is stored per-user in a writable folder, **not** in
the install location:

- **Windows:** `%LOCALAPPDATA%\Bix`
- **macOS:** `~/Library/Application Support/Bix`

## What gets built

| OS | Artifact | Installs to |
|----|----------|-------------|
| Windows x64 | `Bix-<ver>-win-x64.msi` | `C:\Program Files\Bix` + Start-menu shortcut "Bix" |
| macOS Apple Silicon | `Bix-<ver>-osx-arm64.pkg` / `.dmg` | `/Applications/Bix.app` |
| macOS Intel | `Bix-<ver>-osx-x64.pkg` / `.dmg` | `/Applications/Bix.app` |

## How to produce a release

The installers are built by GitHub Actions on real Windows/macOS runners
(`.github/workflows/desktop-release.yml`) — they can't be built from Linux.

**Option A — tag (also publishes a GitHub Release with the files attached):**
```bash
git tag desktop-v1.0.0
git push origin desktop-v1.0.0
```

**Option B — manual run (artifacts only, no release):**
GitHub → Actions → "Desktop Installers" → Run workflow → enter the version.

Download the `.msi` / `.pkg` / `.dmg` from the workflow run's **Artifacts**, or
from the **Release** (tag runs).

## Running the app

- **Windows:** install the MSI, launch **Bix** from the Start menu. A console
  window shows the server log; closing it stops the app. The browser opens
  automatically.
- **macOS:** open the `.pkg` (or `.dmg` → drag `Bix.app` to Applications), then
  launch **Bix**.

## ⚠️ Code signing

These artifacts are **unsigned**, so the OS will warn on first launch:

- **Windows SmartScreen:** "More info" → "Run anyway".
- **macOS Gatekeeper:** right-click `Bix.app` → **Open** (once), or
  `xattr -dr com.apple.quarantine /Applications/Bix.app`.

To ship without warnings, add code-signing certs as repo secrets and a signing
step (Windows: `signtool`; macOS: `codesign` + `notarytool`). Not included here.

## Local desktop run (no installer)

You can run desktop mode directly for testing:
```bash
dotnet run --project Analytika -- --desktop
# or set BIX_DESKTOP=1
```
It binds `http://localhost:5097`, uses the per-user data dir, and opens a browser.
