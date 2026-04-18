using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Input;
using EnvDTE;
using EnvDTE80;
using DevUtils.ExtensionVS.Services;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace DevUtils.ExtensionVS.Commands
{
    internal sealed class FormatAllFilesCommand
    {
        public const int CommandId = 0x0103;
        public static readonly Guid CommandSet = new Guid("a3b4c5d6-e7f8-4901-a2b3-c4d5e6f70001");

        private readonly AsyncPackage _package;

        private FormatAllFilesCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            _package = package;
            var cmdId = new CommandID(CommandSet, CommandId);
            commandService.AddCommand(new OleMenuCommand(Execute, cmdId));
        }

        public static FormatAllFilesCommand Instance { get; private set; }

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            Instance = new FormatAllFilesCommand(package, commandService);
        }

        private void Execute(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _ = ExecuteAsync();
        }

        private async Task ExecuteAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var dte = await _package.GetServiceAsync(typeof(DTE)) as DTE2;
            if (dte == null) return;

            // Determine scope: project or solution
            Project singleProject = null;
            foreach (SelectedItem selected in dte.SelectedItems)
            {
                if (selected.Project != null) { singleProject = selected.Project; break; }
            }

            var isSolution  = singleProject == null;
            var targetPath  = isSolution ? dte.Solution.FullName : singleProject.FullName;
            var scopeDir    = Path.GetDirectoryName(targetPath) ?? string.Empty;
            var scopeName   = isSolution
                ? Path.GetFileNameWithoutExtension(dte.Solution.FullName)
                : singleProject.Name;

            // Auto-detect formatting strategy
            var hasEditorConfig        = HasEditorConfig(scopeDir);
            var dotnetFormatAvailable  = !hasEditorConfig && await IsDotnetFormatAvailableAsync();

            Mouse.OverrideCursor = Cursors.Wait;
            try
            {
                if (dotnetFormatAvailable)
                {
                    // Fast path — run dotnet format in background, output to VS pane
                    Mouse.OverrideCursor = null;
                    await RunDotnetFormatAsync(targetPath, scopeName);
                }
                else
                {
                    // VS formatter path — open each file and apply Edit.FormatDocument
                    var files = new List<(string path, ProjectItem item, bool wasOpen)>();

                    if (isSolution)
                        foreach (Project proj in dte.Solution.Projects)
                            CollectProjectFiles(proj, files);
                    else
                        CollectRecursive(singleProject.ProjectItems, files);

                    var reason = hasEditorConfig ? " (.editorconfig detected)" : string.Empty;
                    DevUtilsPane.Log($"[Dev Utils] Format All Files — VS formatter{reason}, {files.Count} file(s) in '{scopeName}'");
                    DevUtilsPane.Activate();

                    int formatted = 0;
                    foreach (var (path, item, _) in files)
                    {
                        try
                        {
                            var alreadyOpen = item.IsOpen[EnvDTE.Constants.vsViewKindCode];
                            var window = item.Open(EnvDTE.Constants.vsViewKindCode);
                            window.Activate();
                            dte.ExecuteCommand("Edit.FormatDocument");
                            window.Document.Save();
                            if (!alreadyOpen) window.Close();
                            DevUtilsPane.Log($"  [formatted] {path}");
                            formatted++;
                        }
                        catch { }
                    }

                    DevUtilsPane.Log($"[Dev Utils] Format All Files — done. Formatted: {formatted}");

                    Mouse.OverrideCursor = null;
                    VsShellUtilities.ShowMessageBox(
                        _package,
                        $"Formatted {formatted} file(s) in {scopeName}{reason}.",
                        "Format All Files",
                        OLEMSGICON.OLEMSGICON_INFO,
                        OLEMSGBUTTON.OLEMSGBUTTON_OK,
                        OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                }
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }

        // ── Strategy detection ────────────────────────────────────────────────

        private static bool HasEditorConfig(string startDir)
        {
            var dir = startDir;
            while (!string.IsNullOrEmpty(dir))
            {
                if (File.Exists(Path.Combine(dir, ".editorconfig")))
                    return true;
                var parent = Path.GetDirectoryName(dir);
                if (parent == dir) break;
                dir = parent;
            }
            return false;
        }

        private static async System.Threading.Tasks.Task<bool> IsDotnetFormatAvailableAsync()
        {
            return await System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var psi = new ProcessStartInfo("dotnet", "format --version")
                    {
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    using (var p = System.Diagnostics.Process.Start(psi))
                        return p != null && p.WaitForExit(5000) && p.ExitCode == 0;
                }
                catch { return false; }
            });
        }

        // ── dotnet format (fast path) ─────────────────────────────────────────

        private async Task RunDotnetFormatAsync(string targetPath, string scopeName)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            DevUtilsPane.Log($"[Dev Utils] Format All Files — starting dotnet format on '{scopeName}'...");
            DevUtilsPane.Activate();

            var output   = new StringBuilder();
            var exitCode = 0;

            await System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var psi = new ProcessStartInfo("dotnet", $"format \"{targetPath}\"")
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError  = true,
                        UseShellExecute = false,
                        CreateNoWindow  = true,
                    };

                    using (var process = new System.Diagnostics.Process { StartInfo = psi })
                    {
                        process.OutputDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
                        process.ErrorDataReceived  += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
                        process.Start();
                        process.BeginOutputReadLine();
                        process.BeginErrorReadLine();
                        process.WaitForExit();
                        exitCode = process.ExitCode;
                    }
                }
                catch (Exception ex)
                {
                    output.AppendLine($"Error: {ex.Message}");
                    exitCode = -1;
                }
            });

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (output.Length > 0)
                DevUtilsPane.Log(output.ToString().TrimEnd());

            var result = exitCode == 0 ? "completed successfully" : $"failed (exit code {exitCode})";
            DevUtilsPane.Log($"[Dev Utils] Format All Files — dotnet format {result}.");

            VsShellUtilities.ShowMessageBox(
                _package,
                exitCode == 0
                    ? $"Format completed for '{scopeName}'.\nSee the 'Dev Utils' pane in the Output window for details."
                    : $"dotnet format failed (exit {exitCode}) for '{scopeName}'.\nSee the 'Dev Utils' pane in the Output window for details.",
                "Format All Files",
                exitCode == 0 ? OLEMSGICON.OLEMSGICON_INFO : OLEMSGICON.OLEMSGICON_WARNING,
                OLEMSGBUTTON.OLEMSGBUTTON_OK,
                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        }

        // ── DTE helpers ───────────────────────────────────────────────────────

        private void CollectProjectFiles(Project proj, List<(string, ProjectItem, bool)> result)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (proj == null) return;

            if (proj.Kind == "{66A26720-8FB5-11D2-AA7E-00C04F688DDE}")
            {
                if (proj.ProjectItems != null)
                    foreach (ProjectItem item in proj.ProjectItems)
                        if (item.SubProject != null)
                            CollectProjectFiles(item.SubProject, result);
                return;
            }

            if (proj.ProjectItems != null)
                CollectRecursive(proj.ProjectItems, result);
        }

        private void CollectRecursive(ProjectItems items, List<(string, ProjectItem, bool)> result)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (items == null) return;
            foreach (ProjectItem item in items)
            {
                if (item.Kind == EnvDTE.Constants.vsProjectItemKindPhysicalFile)
                {
                    var name = item.Name;
                    if (name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                        && !name.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
                        && !name.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase))
                    {
                        var path = item.FileNames[1];
                        if (path.IndexOf(@"\bin\", StringComparison.OrdinalIgnoreCase) < 0
                            && path.IndexOf(@"\obj\", StringComparison.OrdinalIgnoreCase) < 0)
                            result.Add((path, item, false));
                    }
                }
                CollectRecursive(item.ProjectItems, result);
            }
        }
    }
}
