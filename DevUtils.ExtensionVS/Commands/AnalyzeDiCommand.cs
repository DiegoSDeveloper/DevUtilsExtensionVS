using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.IO;
using System.Windows.Input;
using EnvDTE;
using EnvDTE80;
using DevUtils.ExtensionVS.Models;
using DevUtils.ExtensionVS.Services;
using DevUtils.ExtensionVS.UI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace DevUtils.ExtensionVS.Commands
{
    internal sealed class AnalyzeDiCommand
    {
        public const int CommandId = 0x0102;
        public static readonly Guid CommandSet = new Guid("a3b4c5d6-e7f8-4901-a2b3-c4d5e6f70001");

        private readonly AsyncPackage _package;

        private AnalyzeDiCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            _package = package;
            var cmdId = new CommandID(CommandSet, CommandId);
            commandService.AddCommand(new OleMenuCommand(Execute, cmdId));
        }

        public static AnalyzeDiCommand Instance { get; private set; }

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            Instance = new AnalyzeDiCommand(package, commandService);
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

            Project targetProject = null;
            foreach (SelectedItem selected in dte.SelectedItems)
            {
                if (selected.Project != null)     { targetProject = selected.Project; break; }
                if (selected.ProjectItem != null) { targetProject = selected.ProjectItem.ContainingProject; break; }
            }

            if (targetProject == null)
            {
                VsShellUtilities.ShowMessageBox(
                    _package, "No project found in selection.",
                    "DI Injection Analyzer", OLEMSGICON.OLEMSGICON_INFO,
                    OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                return;
            }

            List<DiRegistration> registrations;
            List<InjectionAnalysis> analyses;

            Mouse.OverrideCursor = Cursors.Wait;
            try
            {
                // Scan DI registrations only from the selected project
                var projectFiles = CollectCsFiles(targetProject.ProjectItems);

                // Scan ALL solution projects for injection targets (consumers may be in other projects)
                var solutionDir = Path.GetDirectoryName(dte.Solution.FullName) ?? string.Empty;
                var allSolutionFiles = CollectAllSolutionCsFiles(dte.Solution);

                var analyzer = new DiAnalyzerService();
                (registrations, analyses) = await analyzer.AnalyzeAsync(
                    solutionDir, projectFiles, allSolutionFiles);
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }

            if (registrations.Count == 0)
            {
                VsShellUtilities.ShowMessageBox(
                    _package, "No DI registrations (AddScoped/AddTransient/AddSingleton) found in this project.\n\nGenerate one first using 'Generate DI Registration'.",
                    "DI Injection Analyzer", OLEMSGICON.OLEMSGICON_INFO,
                    OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                return;
            }

            if (analyses.Count == 0)
            {
                VsShellUtilities.ShowMessageBox(
                    _package,
                    "No injection opportunities found across the solution.\n\n" +
                    "All registered implementations are the only classes, or there are no consumer classes that could receive these dependencies.",
                    "DI Injection Analyzer", OLEMSGICON.OLEMSGICON_INFO,
                    OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                return;
            }

            var dialog = new DiAnalysisDialog(registrations, analyses);
            dialog.ShowModal();
        }

        // ── DTE helpers ───────────────────────────────────────────────────────

        private List<(string path, ProjectItem item)> CollectAllSolutionCsFiles(Solution solution)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var result = new List<(string, ProjectItem)>();
            foreach (Project proj in solution.Projects)
                CollectProjectCsFiles(proj, result);
            return result;
        }

        private void CollectProjectCsFiles(Project proj, List<(string, ProjectItem)> result)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (proj == null) return;

            // Solution folder — recurse into sub-projects
            if (proj.Kind == "{66A26720-8FB5-11D2-AA7E-00C04F688DDE}")
            {
                if (proj.ProjectItems != null)
                    foreach (ProjectItem item in proj.ProjectItems)
                        if (item.SubProject != null)
                            CollectProjectCsFiles(item.SubProject, result);
                return;
            }

            if (proj.ProjectItems != null)
                CollectRecursive(proj.ProjectItems, result);
        }

        private List<(string path, ProjectItem item)> CollectCsFiles(ProjectItems items)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var result = new List<(string, ProjectItem)>();
            CollectRecursive(items, result);
            return result;
        }

        private void CollectRecursive(ProjectItems items, List<(string, ProjectItem)> result)
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
                            result.Add((path, item));
                    }
                }
                CollectRecursive(item.ProjectItems, result);
            }
        }
    }
}
