using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.PointClouds;
using Autodesk.Revit.UI;
using WinForms = System.Windows.Forms;

namespace LASPointCloudEngine;

/// <summary>
/// Starts Autodesk ReCap Pro's supported background LAS-to-RCP converter.  This deliberately
/// lives outside the custom point-cloud engine and never performs conversion work in a Revit
/// graphics callback.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class CreateIndexedRcpFromLasCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        if (ReCapProjectWorkflow.HasActiveConversion)
        {
            ReCapProjectWorkflow.ShowStatus(commandData.Application.MainWindowHandle);
            return Result.Cancelled;
        }

        string? deCapPath = ReCapProjectWorkflow.FindDeCapExecutable();
        if (deCapPath is null)
        {
            Autodesk.Revit.UI.TaskDialog.Show(
                "Create Indexed RCP",
                "Autodesk ReCap Pro's DeCap.exe was not found. Install ReCap Pro 2027, then restart Revit.");
            return Result.Cancelled;
        }

        // DeCap uses the same activated license as ReCap Pro.  Let the user finish activation in
        // ReCap itself rather than trying to automate sign-in or queue two ReCap jobs at once.
        if (ReCapProjectWorkflow.IsReCapRunning())
        {
            Autodesk.Revit.UI.TaskDialog.Show(
                "Create Indexed RCP",
                "Autodesk ReCap is currently open. Complete sign-in/activation there, close ReCap, then run this command again.\n\n" +
                "The conversion will run in the background after ReCap is closed, so Revit can remain usable.");
            return Result.Cancelled;
        }

        if (ReCapProjectWorkflow.IsDeCapRunning())
        {
            Autodesk.Revit.UI.TaskDialog.Show(
                "Create Indexed RCP",
                "An Autodesk ReCap background conversion is already running. Wait for it to finish, then use RCP Conversion Status or link its completed RCP/RCS dataset.");
            return Result.Cancelled;
        }

        if (!ReCapProjectWorkflow.TryCheckLicense(deCapPath, out string licenseDetail))
        {
            Autodesk.Revit.UI.TaskDialog.Show(
                "Activate ReCap Pro",
                "ReCap Pro is installed, but its converter does not yet have an active license.\n\n" +
                "Open Autodesk ReCap, sign in with the licensed Autodesk account or activate the trial, then close ReCap and run this command again.\n\n" +
                licenseDetail);
            return Result.Cancelled;
        }

        using WinForms.OpenFileDialog sourcePicker = new()
        {
            Title = "Select the LAS file to index with ReCap Pro",
            Filter = "LAS point cloud (*.las)|*.las",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (sourcePicker.ShowDialog() != WinForms.DialogResult.OK)
            return Result.Cancelled;

        try
        {
            _ = LasHeader.Read(sourcePicker.FileName);
        }
        catch (Exception exception)
        {
            Autodesk.Revit.UI.TaskDialog.Show(
                "Create Indexed RCP",
                "The selected LAS file cannot be used.\n\n" + exception.Message);
            return Result.Cancelled;
        }

        if (!ReCapProjectWorkflow.TryChooseInputUnit(out int inputUnit))
            return Result.Cancelled;

        string sourceName = Path.GetFileNameWithoutExtension(sourcePicker.FileName);
        using WinForms.SaveFileDialog destinationPicker = new()
        {
            Title = "Choose a permanent location for the indexed ReCap project",
            Filter = "ReCap project (*.rcp)|*.rcp",
            DefaultExt = "rcp",
            AddExtension = true,
            CheckPathExists = true,
            OverwritePrompt = false,
            FileName = sourceName + ".rcp",
            InitialDirectory = Path.GetDirectoryName(sourcePicker.FileName),
        };
        if (destinationPicker.ShowDialog() != WinForms.DialogResult.OK)
            return Result.Cancelled;

        string rcpPath = Path.GetFullPath(destinationPicker.FileName);
        string? outputFolder = Path.GetDirectoryName(rcpPath);
        if (!ReCapProjectWorkflow.TryValidateProjectDestination(rcpPath, outputFolder, out string destinationError))
        {
            Autodesk.Revit.UI.TaskDialog.Show("Create Indexed RCP", destinationError);
            return Result.Cancelled;
        }

        if (File.Exists(rcpPath) || ReCapProjectWorkflow.HasExistingSupportFolder(outputFolder!, rcpPath))
        {
            Autodesk.Revit.UI.TaskDialog.Show(
                "Create Indexed RCP",
                "An RCP or matching ReCap Support folder already exists at that location. Choose a new, empty project file name instead; this command never overwrites or mixes indexed point-cloud data.");
            return Result.Cancelled;
        }

        if (!ReCapProjectWorkflow.HasRecommendedFreeSpace(outputFolder!, sourcePicker.FileName, out string spaceWarning))
        {
            Autodesk.Revit.UI.TaskDialog warning = new("Create Indexed RCP")
            {
                MainInstruction = "The selected drive may not have enough free space.",
                MainContent = spaceWarning + "\n\nChoose another local folder or free space before conversion.",
                CommonButtons = TaskDialogCommonButtons.Cancel,
            };
            warning.Show();
            return Result.Cancelled;
        }

        if (!ReCapProjectWorkflow.TryStartConversion(
                deCapPath,
                sourcePicker.FileName,
                outputFolder,
                Path.GetFileName(rcpPath),
                inputUnit,
                out string startError))
        {
            Autodesk.Revit.UI.TaskDialog.Show("Create Indexed RCP", startError);
            return Result.Failed;
        }

        Autodesk.Revit.UI.TaskDialog.Show(
            "Indexed RCP Conversion Started",
            "ReCap is indexing the LAS in the background with zero decimation and no custom range-clipping settings. Revit remains usable.\n\n" +
            "The live RCP Indexing Progress window will now show activity. Keep both the original LAS and the selected RCP folder in place. When conversion finishes, use Link Indexed RCP/RCS on the LAS Point Cloud tab. Do not remove the current LAS link until the native RCP is validated.");
        ReCapProjectWorkflow.ShowStatus(commandData.Application.MainWindowHandle);
        return Result.Succeeded;
    }
}

/// <summary>
/// Links the completed RCP/RCS through Revit's native indexed point-cloud engine.  It never
/// deletes, hides, reloads, or replaces an existing custom LAS link.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class LinkIndexedRcpCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        Document? document = commandData.Application.ActiveUIDocument?.Document;
        if (document is null || document.IsFamilyDocument)
        {
            Autodesk.Revit.UI.TaskDialog.Show("Link Indexed RCP/RCS", "Open a Revit project before linking a point cloud.");
            return Result.Cancelled;
        }

        using WinForms.OpenFileDialog picker = new()
        {
            Title = "Select a completed ReCap RCP or RCS point cloud",
            Filter = "ReCap project (*.rcp)|*.rcp|ReCap scan (*.rcs)|*.rcs",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (picker.ShowDialog() != WinForms.DialogResult.OK)
            return Result.Cancelled;

        string extension = Path.GetExtension(picker.FileName);
        string engine = extension.Equals(".rcp", StringComparison.OrdinalIgnoreCase) ? "rcp" :
            extension.Equals(".rcs", StringComparison.OrdinalIgnoreCase) ? "rcs" : string.Empty;
        if (engine.Length == 0)
        {
            Autodesk.Revit.UI.TaskDialog.Show("Link Indexed RCP/RCS", "Choose an RCP or RCS file.");
            return Result.Cancelled;
        }

        bool nativeEngineAvailable = PointCloudEngineRegistry.GetSupportedEngines()
            .Any(identifier => string.Equals(identifier, engine, StringComparison.OrdinalIgnoreCase));
        if (!nativeEngineAvailable)
        {
            Autodesk.Revit.UI.TaskDialog.Show(
                "Link Indexed RCP/RCS",
                "Revit's native " + engine.ToUpperInvariant() + " point-cloud engine is unavailable in this session.");
            return Result.Failed;
        }

        try
        {
            using Transaction transaction = new(document, "Link Indexed ReCap Point Cloud");
            transaction.Start();
            PointCloudType cloudType = PointCloudType.Create(document, engine, picker.FileName);
            PointCloudInstance.Create(document, cloudType.Id, Transform.Identity);
            transaction.Commit();

            Autodesk.Revit.UI.TaskDialog.Show(
                "Indexed RCP/RCS Linked",
                "The native ReCap point cloud was linked at the project origin. Keep its RCP and support files together in the selected folder, then verify coordinates and visibility before removing any older LAS link.");
            return Result.Succeeded;
        }
        catch (Exception exception)
        {
            message = exception.Message;
            Autodesk.Revit.UI.TaskDialog.Show("Link Indexed RCP/RCS", "The ReCap point cloud could not be linked.\n\n" + exception.Message);
            return Result.Failed;
        }
    }
}

[Transaction(TransactionMode.Manual)]
public sealed class ReCapConversionStatusCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        ReCapProjectWorkflow.ShowStatus(commandData.Application.MainWindowHandle);
        return Result.Succeeded;
    }
}

internal static class ReCapProjectWorkflow
{
    private const long RecommendedFreeSpaceBytes = 15L * 1024 * 1024 * 1024;
    private static readonly object Sync = new();
    private static ReCapConversionJob? _currentJob;
    private static ReCapConversionProgressForm? _progressWindow;

    public static bool HasActiveConversion
    {
        get
        {
            lock (Sync)
                return _currentJob is { IsFinished: false };
        }
    }

    public static string? FindDeCapExecutable()
    {
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string candidate = Path.Combine(programFiles, "Autodesk", "Autodesk ReCap", "decap.exe");
        return File.Exists(candidate) ? candidate : null;
    }

    public static bool IsReCapRunning()
    {
        try
        {
            return Process.GetProcessesByName("ReCap").Any();
        }
        catch
        {
            // A conservative failure avoids running a converter while a ReCap session might be open.
            return true;
        }
    }

    public static bool IsDeCapRunning()
    {
        try
        {
            return Process.GetProcessesByName("DeCap").Any();
        }
        catch
        {
            // Do not risk starting a second conversion when process discovery is unavailable.
            return true;
        }
    }

    public static bool TryCheckLicense(string deCapPath, out string detail)
    {
        detail = string.Empty;
        try
        {
            using Process process = new()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = deCapPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
            };
            process.StartInfo.ArgumentList.Add("--checkLicense");
            if (!process.Start())
            {
                detail = "ReCap's license checker could not be started.";
                return false;
            }

            Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
            Task<string> standardError = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(45_000))
            {
                try { process.Kill(entireProcessTree: true); }
                catch { /* The short-lived check may have already ended. */ }
                detail = "ReCap's license check did not finish. Open ReCap Pro, complete activation, close it, and try again.";
                return false;
            }

            string output = (standardOutput.GetAwaiter().GetResult() + Environment.NewLine +
                             standardError.GetAwaiter().GetResult()).Trim();
            // DeCap 2027 uses both "License Check PASSED" and the numeric-status form
            // "[M][2049][License Check OK]" for a successful entitlement check.
            // A failure still takes precedence if a future DeCap build writes both statuses.
            bool licensePassed = Regex.IsMatch(
                output,
                @"\bLicense\s+Check\s+(?:OK|PASSED)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            bool licenseFailure = Regex.IsMatch(
                output,
                @"\bLicense\s+Check\s+(?:FAILED|FAIL)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
                                  Regex.IsMatch(
                                      output,
                                      @"\bLicensing\s+Error\b",
                                      RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (process.ExitCode != 0 || licenseFailure || !licensePassed)
            {
                detail = !licenseFailure && process.ExitCode == 0
                    ? "ReCap's license status was indeterminate. Wait a moment, confirm ReCap is signed in and activated, then try again.\n\n" + Shorten(output, 1_500)
                    : Shorten(output, 1_500);
                return false;
            }

            return true;
        }
        catch (Exception exception)
        {
            detail = exception.Message;
            return false;
        }
    }

    public static bool TryChooseInputUnit(out int unitType)
    {
        unitType = 2;
        using WinForms.Form dialog = new()
        {
            Text = "LAS source units",
            Width = 560,
            Height = 215,
            FormBorderStyle = WinForms.FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            StartPosition = WinForms.FormStartPosition.CenterParent,
        };

        WinForms.Label description = new()
        {
            AutoSize = false,
            Left = 18,
            Top = 16,
            Width = 510,
            Height = 52,
            Text = "Choose the linear units used by the LAS source. This affects the RCP scale. " +
                   "The existing LAS engine assumed metres, but confirm this with the survey data if possible.",
        };
        WinForms.ComboBox units = new()
        {
            Left = 18,
            Top = 78,
            Width = 510,
            DropDownStyle = WinForms.ComboBoxStyle.DropDownList,
            DisplayMember = nameof(InputUnitOption.DisplayName),
        };
        units.Items.AddRange(new object[]
        {
            new InputUnitOption("Millimetres", 0),
            new InputUnitOption("Centimetres", 1),
            new InputUnitOption("Metres — recommended for this LAS", 2),
            new InputUnitOption("Inches", 3),
            new InputUnitOption("International feet", 4),
            new InputUnitOption("US survey feet", 5),
        });
        units.SelectedIndex = 2;

        WinForms.Button start = new()
        {
            Text = "Start conversion",
            DialogResult = WinForms.DialogResult.OK,
            Left = 312,
            Top = 122,
            Width = 104,
        };
        WinForms.Button cancel = new()
        {
            Text = "Cancel",
            DialogResult = WinForms.DialogResult.Cancel,
            Left = 424,
            Top = 122,
            Width = 104,
        };
        dialog.Controls.AddRange(new WinForms.Control[] { description, units, start, cancel });
        dialog.AcceptButton = start;
        dialog.CancelButton = cancel;

        if (dialog.ShowDialog() != WinForms.DialogResult.OK || units.SelectedItem is not InputUnitOption selected)
            return false;

        unitType = selected.ReCapUnitType;
        return true;
    }

    public static bool HasRecommendedFreeSpace(string outputFolder, string sourcePath, out string warning)
    {
        warning = string.Empty;
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(outputFolder));
            if (string.IsNullOrWhiteSpace(root))
            {
                warning = "Windows could not identify the selected output drive.";
                return false;
            }

            DriveInfo drive = new(root);
            long sourceBytes = new FileInfo(sourcePath).Length;
            long recommendedBytes = sourceBytes <= long.MaxValue / 2
                ? Math.Max(RecommendedFreeSpaceBytes, sourceBytes * 2)
                : long.MaxValue;
            if (drive.AvailableFreeSpace >= recommendedBytes)
                return true;

            warning = "The selected drive has " + FormatBytes(drive.AvailableFreeSpace) +
                      " free. The selected LAS is " + FormatBytes(sourceBytes) +
                      "; allow at least " + FormatBytes(recommendedBytes) +
                      " for ReCap's RCP/RCS output and working files.";
            return false;
        }
        catch
        {
            warning = "Windows could not confirm the free space on the selected local drive.";
            return false;
        }
    }

    public static bool TryValidateProjectDestination(string rcpPath, string? outputFolder, out string error)
    {
        error = string.Empty;
        if (outputFolder is null || !Directory.Exists(outputFolder))
        {
            error = "Choose an existing local output folder.";
            return false;
        }

        if (!Path.GetExtension(rcpPath).Equals(".rcp", StringComparison.OrdinalIgnoreCase))
        {
            error = "Use the .rcp extension for the ReCap project file.";
            return false;
        }

        string projectName = Path.GetFileNameWithoutExtension(rcpPath);
        if (string.IsNullOrWhiteSpace(projectName) || projectName.EndsWith(' ') || projectName.EndsWith('.'))
        {
            error = "Choose an RCP file name that does not end in a space or period.";
            return false;
        }

        if (projectName.Any(character => !char.IsLetterOrDigit(character) && character != ' ' && character != '-' && character != '_'))
        {
            error = "Use only letters, numbers, spaces, hyphens, or underscores in the RCP file name. This avoids ReCap command-line path issues.";
            return false;
        }

        if (outputFolder.StartsWith(@"\\", StringComparison.Ordinal) ||
            outputFolder.IndexOf(@"\OneDrive", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            error = "Choose a permanent local folder that is not a network share or OneDrive. ReCap RCP and RCS support files must remain together on a local drive.";
            return false;
        }

        try
        {
            string? root = Path.GetPathRoot(outputFolder);
            if (string.IsNullOrWhiteSpace(root) || new DriveInfo(root).DriveType != DriveType.Fixed)
            {
                error = "Choose a folder on a fixed local drive. Network, removable, and cloud-synced folders are not safe for a ReCap RCP/RCS dataset.";
                return false;
            }
        }
        catch
        {
            error = "Windows could not verify that the output folder is on a fixed local drive.";
            return false;
        }

        return true;
    }

    public static bool HasExistingSupportFolder(string outputFolder, string rcpPath)
    {
        string projectName = Path.GetFileNameWithoutExtension(rcpPath);
        string expectedSupportFolder = Path.Combine(outputFolder, projectName + " Support");
        return Directory.Exists(expectedSupportFolder);
    }

    public static bool TryStartConversion(
        string deCapPath,
        string sourcePath,
        string outputFolder,
        string projectFileName,
        int inputUnitType,
        out string error)
    {
        lock (Sync)
        {
            if (_currentJob is { IsFinished: false })
            {
                error = "A ReCap conversion is already running. Check RCP Conversion Status before starting another one.";
                return false;
            }

            ReCapConversionJob job = new(deCapPath, sourcePath, outputFolder, projectFileName, inputUnitType);
            if (!job.TryStart(out error))
                return false;

            _currentJob = job;
            return true;
        }
    }

    public static void ShowStatus(IntPtr ownerHandle = default)
    {
        ReCapConversionSnapshot? status;
        lock (Sync)
            status = _currentJob?.GetSnapshot();

        if (status is null)
        {
            Autodesk.Revit.UI.TaskDialog.Show(
                "RCP Indexing Progress",
                "No ReCap conversion has been started in this Revit session.\n\n" +
                "If an RCP was already created, use Link Indexed RCP/RCS to attach it.");
            return;
        }

        ReCapConversionProgressForm? existingWindow;
        ReCapConversionProgressForm? newWindow = null;
        lock (Sync)
        {
            existingWindow = _progressWindow;
            if (existingWindow is null || existingWindow.IsDisposed)
            {
                ReCapConversionProgressForm createdWindow = new(GetCurrentSnapshot);
                createdWindow.FormClosed += (_, _) => ClearProgressWindow(createdWindow);
                _progressWindow = createdWindow;
                newWindow = createdWindow;
            }
        }

        if (newWindow is not null)
        {
            if (ownerHandle != IntPtr.Zero)
                newWindow.Show(new RevitWindowHandle(ownerHandle));
            else
                newWindow.Show();
            return;
        }

        if (existingWindow is not null)
        {
            if (existingWindow.WindowState == WinForms.FormWindowState.Minimized)
                existingWindow.WindowState = WinForms.FormWindowState.Normal;
            existingWindow.BringToFront();
            existingWindow.Activate();
        }
    }

    private static ReCapConversionSnapshot? GetCurrentSnapshot()
    {
        lock (Sync)
            return _currentJob?.GetSnapshot();
    }

    private static void ClearProgressWindow(ReCapConversionProgressForm window)
    {
        lock (Sync)
        {
            if (ReferenceEquals(_progressWindow, window))
                _progressWindow = null;
        }
    }

    private static string Shorten(string text, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "ReCap did not return a license status. Open ReCap Pro, confirm activation, close it, and try again.";
        return text.Length <= maximumLength ? text : text[^maximumLength..];
    }

    private static string FormatBytes(long bytes)
    {
        double gib = bytes / (1024d * 1024d * 1024d);
        return gib.ToString("0.0", CultureInfo.InvariantCulture) + " GB";
    }

    private sealed class InputUnitOption
    {
        public InputUnitOption(string displayName, int reCapUnitType)
        {
            DisplayName = displayName;
            ReCapUnitType = reCapUnitType;
        }

        public string DisplayName { get; }
        public int ReCapUnitType { get; }
        public override string ToString() => DisplayName;
    }
}

internal sealed class ReCapConversionJob
{
    private const int MaximumCapturedOutputCharacters = 1_000_000;
    // This undocumented DeCap 2027 stdout grammar was captured from the installed converter's
    // LAS-to-RCP log: [P][1 42] means the first input is at 42%. If a future converter omits it,
    // the progress window deliberately falls back to its indeterminate animated bar.
    private static readonly Regex PercentagePattern = new(
        @"^\s*\[P\]\[\d+\s+(?<percentage>100|[1-9]?\d)\]\s*$",
        RegexOptions.CultureInvariant);
    private readonly object _gate = new();
    private readonly string _deCapPath;
    private readonly string _sourcePath;
    private readonly string _outputFolder;
    private readonly string _projectFileName;
    private readonly int _inputUnitType;
    private readonly string _expectedRcpPath;
    private readonly string _expectedSupportFolder;
    private string _state = "Preparing ReCap conversion";
    private string _detail = "Waiting for Autodesk ReCap's background converter to start.";
    private string? _logPath;
    private string? _lastConverterMessage;
    private DateTimeOffset? _startedUtc;
    private int? _nativeProgressPercent;
    private bool _isFinished;

    public ReCapConversionJob(
        string deCapPath,
        string sourcePath,
        string outputFolder,
        string projectFileName,
        int inputUnitType)
    {
        _deCapPath = deCapPath;
        _sourcePath = sourcePath;
        _outputFolder = outputFolder;
        _projectFileName = projectFileName;
        _inputUnitType = inputUnitType;
        _expectedRcpPath = Path.Combine(outputFolder, projectFileName);
        _expectedSupportFolder = Path.Combine(outputFolder, Path.GetFileNameWithoutExtension(projectFileName) + " Support");
    }

    public bool IsFinished
    {
        get
        {
            lock (_gate)
                return _isFinished;
        }
    }

    public bool TryStart(out string error)
    {
        if (File.Exists(_expectedRcpPath) || Directory.Exists(_expectedSupportFolder))
        {
            error = "An RCP or matching ReCap Support folder already exists at the selected destination. Choose a new project file name.";
            return false;
        }

        try
        {
            _ = Task.Run(RunAsync);
            error = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    public ReCapConversionSnapshot GetSnapshot()
    {
        lock (_gate)
            return new ReCapConversionSnapshot(
                _state,
                _detail,
                _expectedRcpPath,
                _logPath,
                _lastConverterMessage,
                _startedUtc,
                _nativeProgressPercent,
                _isFinished,
                string.Equals(_state, "Indexed RCP is ready", StringComparison.Ordinal));
    }

    private async Task RunAsync()
    {
        string standardOutput = string.Empty;
        string standardError = string.Empty;
        int? exitCode = null;
        StringBuilder standardOutputBuilder = new();
        StringBuilder standardErrorBuilder = new();
        object streamGate = new();
        try
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = _deCapPath,
                WorkingDirectory = _outputFolder,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("--importWithLicense");
            startInfo.ArgumentList.Add(_outputFolder);
            startInfo.ArgumentList.Add(_projectFileName);
            startInfo.ArgumentList.Add("--decimation");
            startInfo.ArgumentList.Add("0");
            startInfo.ArgumentList.Add("--inputUnitType");
            startInfo.ArgumentList.Add(_inputUnitType.ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add(_sourcePath);

            using Process process = new() { StartInfo = startInfo };
            TaskCompletionSource<bool> standardOutputCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> standardErrorCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            process.OutputDataReceived += (_, eventArgs) =>
            {
                if (eventArgs.Data is null)
                    standardOutputCompleted.TrySetResult(true);
                else
                    CaptureConverterLine(eventArgs.Data, standardOutputBuilder, streamGate, isError: false);
            };
            process.ErrorDataReceived += (_, eventArgs) =>
            {
                if (eventArgs.Data is null)
                    standardErrorCompleted.TrySetResult(true);
                else
                    CaptureConverterLine(eventArgs.Data, standardErrorBuilder, streamGate, isError: true);
            };
            if (!process.Start())
                throw new InvalidOperationException("Autodesk ReCap's background converter could not be started.");

            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
            catch { /* ReCap may retain its normal priority when Windows denies this optional hint. */ }

            lock (_gate)
            {
                _startedUtc = DateTimeOffset.UtcNow;
                _nativeProgressPercent = null;
                _lastConverterMessage = null;
                _state = "ReCap conversion running";
                _detail = "ReCap is indexing the LAS in the background. Waiting for ReCap's first progress marker.";
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync().ConfigureAwait(false);
            await Task.WhenAll(standardOutputCompleted.Task, standardErrorCompleted.Task).ConfigureAwait(false);
            lock (streamGate)
            {
                standardOutput = standardOutputBuilder.ToString();
                standardError = standardErrorBuilder.ToString();
            }
            exitCode = process.ExitCode;

            SetLogPath(TryWriteLog(standardOutput, standardError, exitCode));
            if (exitCode != 0)
            {
                Finish("ReCap conversion failed", "DeCap exited with code " + exitCode + ". " + DescribeOutput(standardOutput, standardError));
                return;
            }

            if (!HasExpectedReCapDataset(out string datasetProblem))
            {
                Finish(
                    "ReCap conversion needs attention",
                    "DeCap finished but " + datasetProblem + " Check the conversion log and confirm ReCap activation and output-folder permissions. " +
                    DescribeOutput(standardOutput, standardError));
                return;
            }

            Finish(
                "Indexed RCP is ready",
                "ReCap created and verified the RCP/RCS dataset successfully. Use Link Indexed RCP/RCS, then validate it before removing the previous LAS link.",
                finalNativeProgressPercent: 100);
        }
        catch (Exception exception)
        {
            lock (streamGate)
            {
                if (standardOutput.Length == 0)
                    standardOutput = standardOutputBuilder.ToString();
                if (standardError.Length == 0)
                    standardError = standardErrorBuilder.ToString();
            }
            if (GetLogPath() is null)
                SetLogPath(TryWriteLog(standardOutput, standardError, exitCode));
            Finish("ReCap conversion failed", exception.Message);
        }
    }

    private void CaptureConverterLine(string line, StringBuilder destination, object streamGate, bool isError)
    {
        lock (streamGate)
            AppendCapturedLine(destination, line);

        Match progressMatch = isError ? Match.Empty : PercentagePattern.Match(line);
        int nativePercentage = 0;
        bool hasProgress = progressMatch.Success &&
            int.TryParse(progressMatch.Groups["percentage"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out nativePercentage);

        lock (_gate)
        {
            if (_isFinished)
                return;

            string conciseMessage = ShortenConverterMessage(line);
            _lastConverterMessage = isError ? "ReCap diagnostic: " + conciseMessage : conciseMessage;
            if (hasProgress)
            {
                int boundedPercentage = Math.Clamp(nativePercentage, 0, 100);
                _nativeProgressPercent = Math.Max(_nativeProgressPercent ?? 0, boundedPercentage);
                _detail = boundedPercentage >= 100
                    ? "ReCap finished its indexing pass. Verifying the RCP and support/RCS files before marking the job complete."
                    : "ReCap reports " + boundedPercentage.ToString(CultureInfo.InvariantCulture) + "% indexed. The RCP/RCS dataset will be verified before completion.";
            }
            else if (isError)
            {
                _detail = "ReCap reported a diagnostic; indexing may still be continuing. Latest message: " + conciseMessage;
            }
            else if (line.StartsWith("[R]", StringComparison.OrdinalIgnoreCase))
            {
                _detail = "ReCap wrote the RCP project file. Verifying its support/RCS files before completion.";
            }
            else if (line.StartsWith("[F]", StringComparison.OrdinalIgnoreCase))
            {
                _detail = "ReCap queued the LAS source and is beginning the indexing pass.";
            }
        }
    }

    private void Finish(string state, string detail, int? finalNativeProgressPercent = null)
    {
        lock (_gate)
        {
            if (finalNativeProgressPercent.HasValue)
                _nativeProgressPercent = Math.Clamp(finalNativeProgressPercent.Value, 0, 100);
            _state = state;
            _detail = detail;
            _isFinished = true;
        }
    }

    private void SetLogPath(string? logPath)
    {
        lock (_gate)
            _logPath = logPath;
    }

    private string? GetLogPath()
    {
        lock (_gate)
            return _logPath;
    }

    private static string ShortenConverterMessage(string line)
    {
        const int maximumLength = 260;
        string trimmed = line.Trim();
        return trimmed.Length <= maximumLength ? trimmed : trimmed[^maximumLength..];
    }

    private static void AppendCapturedLine(StringBuilder destination, string line)
    {
        destination.AppendLine(line);
        if (destination.Length > MaximumCapturedOutputCharacters)
            destination.Remove(0, destination.Length - MaximumCapturedOutputCharacters);
    }

    private bool HasExpectedReCapDataset(out string problem)
    {
        if (!File.Exists(_expectedRcpPath))
        {
            problem = "the expected RCP was not found.";
            return false;
        }

        if (!Directory.Exists(_expectedSupportFolder))
        {
            problem = "the matching ReCap Support folder was not found.";
            return false;
        }

        try
        {
            bool containsRcs = Directory.EnumerateFiles(_expectedSupportFolder, "*.rcs", SearchOption.AllDirectories).Any();
            if (!containsRcs)
            {
                problem = "the matching ReCap Support folder contains no RCS scan files.";
                return false;
            }
        }
        catch (Exception exception)
        {
            problem = "the matching ReCap Support folder could not be inspected (" + exception.Message + ").";
            return false;
        }

        problem = string.Empty;
        return true;
    }

    private string? TryWriteLog(string standardOutput, string standardError, int? exitCode)
    {
        try
        {
            string logName = Path.GetFileNameWithoutExtension(_projectFileName) + ".recap-conversion.log";
            string path = Path.Combine(_outputFolder, logName);
            StringBuilder text = new();
            text.AppendLine("Autodesk ReCap LAS-to-RCP conversion");
            text.AppendLine("Source: " + _sourcePath);
            text.AppendLine("Expected RCP: " + _expectedRcpPath);
            text.AppendLine("Expected support folder: " + _expectedSupportFolder);
            text.AppendLine("Input unit type: " + _inputUnitType);
            text.AppendLine("Decimation: 0");
            text.AppendLine("Exit code: " + (exitCode?.ToString(CultureInfo.InvariantCulture) ?? "not available"));
            text.AppendLine();
            text.AppendLine("Standard output:");
            text.AppendLine(standardOutput);
            text.AppendLine();
            text.AppendLine("Standard error:");
            text.AppendLine(standardError);
            File.WriteAllText(path, text.ToString(), Encoding.UTF8);
            return path;
        }
        catch
        {
            return null;
        }
    }

    private static string DescribeOutput(string standardOutput, string standardError)
    {
        string combined = (standardOutput + Environment.NewLine + standardError).Trim();
        if (string.IsNullOrWhiteSpace(combined))
            return "No diagnostic text was returned.";
        const int maximumLength = 1_000;
        return combined.Length <= maximumLength ? combined : combined[^maximumLength..];
    }
}

internal sealed class ReCapConversionSnapshot
{
    public ReCapConversionSnapshot(
        string state,
        string detail,
        string expectedRcpPath,
        string? logPath,
        string? lastConverterMessage,
        DateTimeOffset? startedUtc,
        int? nativeProgressPercent,
        bool isFinished,
        bool isReady)
    {
        State = state;
        Detail = detail;
        ExpectedRcpPath = expectedRcpPath;
        LogPath = logPath;
        LastConverterMessage = lastConverterMessage;
        StartedUtc = startedUtc;
        NativeProgressPercent = nativeProgressPercent;
        IsFinished = isFinished;
        IsReady = isReady;
    }

    public string State { get; }
    public string Detail { get; }
    public string ExpectedRcpPath { get; }
    public string? LogPath { get; }
    public string? LastConverterMessage { get; }
    public DateTimeOffset? StartedUtc { get; }
    public int? NativeProgressPercent { get; }
    public bool IsFinished { get; }
    public bool IsReady { get; }
}
