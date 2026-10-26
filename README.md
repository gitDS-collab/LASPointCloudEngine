[INSTALL.md](https://github.com/user-attachments/files/33967168/INSTALL.md)
# LAS Point Cloud Engine V7 — Revit 2027

This package installs the LAS Point Cloud Engine for **Autodesk Revit 2027** in the current Windows user's add-in folder. It includes the native LAS preview engine, the ReCap RCP/RCS workflow, live RCP indexing progress, and ribbon icons for all four LAS Point Cloud commands.

## Requirements

- Windows with **Autodesk Revit 2027** installed.
- Revit must be fully closed before installation.
- No administrator account is required.
- Use an uncompressed `.las` file for the direct LAS command. Compressed `.laz` files are not supported by this add-in.
- Autodesk ReCap Pro is optional for direct LAS preview, but required for **Create Indexed RCP from LAS**. ReCap must be signed in and licensed for that conversion command.

## Install

1. Download and extract the entire ZIP to a local folder. Do not run the installer inside the ZIP preview window.
2. Close every Revit 2027 window and wait until `Revit.exe` has exited.
3. Right-click `Install-LASPointCloudEngine.ps1` and choose **Run with PowerShell**. If Windows asks for confirmation, approve it.
4. If script execution is blocked, open PowerShell in the extracted folder and run:

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\Install-LASPointCloudEngine.ps1
   ```

5. Wait for the green success message, then start Revit 2027.
6. Open a project and select the **LAS Point Cloud** ribbon tab.

The installer stages and validates the payload first, copies the full plugin to:

```text
%APPDATA%\Autodesk\Revit\Addins\2027\LASPointCloudEngine-v7-icons
```

It updates `%APPDATA%\Autodesk\Revit\Addins\2027\LASPointCloudEngine.addin` only after that payload is ready. Any existing manifest or same-version add-in folder is backed up beside the new installation with a timestamped `.backup-...` name.
Before it changes the add-in folder, the installer verifies the SHA-256 checksums in `checksums.sha256` and confirms that Revit 2027's `RevitAPI.dll` is present.

## Commands in Revit

- **Import LAS Point Cloud** — links a safe, external uncompressed LAS preview without ReCap.
- **Create Indexed RCP from LAS** — runs the installed ReCap converter with zero requested decimation, keeping Revit responsive while indexing happens externally.
- **Link Indexed RCP/RCS** — links a completed ReCap point-cloud dataset with Revit's native point-cloud engine.
- **RCP Indexing Progress** — shows the conversion/indexing state for a ReCap job started in the current Revit session.

The point-cloud icons identify these commands directly on the LAS Point Cloud ribbon tab.

## If Revit reports that the add-in DLL is missing

1. Close Revit.
2. Confirm that this file exists:

   ```text
   %APPDATA%\Autodesk\Revit\Addins\2027\LASPointCloudEngine-v7-icons\LASPointCloudEngine.dll
   ```

3. Run the installer again from a fully extracted ZIP.
4. If you need to roll back, copy the newest `LASPointCloudEngine.addin.backup-...` file over `LASPointCloudEngine.addin`, then restart Revit. The prior V6 manifest is retained by the installer.

## Package contents

- `Install-LASPointCloudEngine.ps1` — current-user installer with backups and verification.
- `checksums.sha256` — integrity list verified by the installer before deployment.
- `payload` — deployable DLL, dependencies, manifest template, and 16px/32px ribbon icons.
- `cache-builder` — optional companion utility for building a safe LAS preview cache outside Revit.
- `source` — C# source code and assets used to build this release.

The RCP/RCS workflow keeps its output RCP and companion RCS/support files together in the permanent folder selected during conversion. Do not move only the `.rcp` file afterward.
