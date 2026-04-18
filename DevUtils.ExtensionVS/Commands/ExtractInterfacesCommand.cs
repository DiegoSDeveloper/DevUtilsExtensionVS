using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using DevUtils.ExtensionVS.Models;
using DevUtils.ExtensionVS.Services;
using DevUtils.ExtensionVS.UI;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace DevUtils.ExtensionVS.Commands
{
    internal sealed class ExtractInterfacesCommand
    {
        public const int CommandId = 0x0100;
        public static readonly Guid CommandSet = new Guid("a3b4c5d6-e7f8-4901-a2b3-c4d5e6f70001");

        private static readonly HashSet<string> IgnoredFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "AssemblyInfo.cs", "GlobalUsings.cs", "GlobalUsings.g.cs"
        };

        private readonly AsyncPackage _package;

        private ExtractInterfacesCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            _package = package;
            var cmdId = new CommandID(CommandSet, CommandId);
            commandService.AddCommand(new OleMenuCommand(Execute, cmdId));
        }

        public static ExtractInterfacesCommand Instance { get; private set; }

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            Instance = new ExtractInterfacesCommand(package, commandService);
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

            // Collect selected files and determine which project(s) to scan
            var selectedFilePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Project targetProject = null;

            foreach (SelectedItem selected in dte.SelectedItems)
            {
                if (selected.Project != null)
                {
                    targetProject = targetProject ?? selected.Project;
                    CollectCsFilePaths(selected.Project.ProjectItems, selectedFilePaths);
                }
                else if (selected.ProjectItem != null)
                {
                    targetProject = targetProject ?? selected.ProjectItem.ContainingProject;
                    CollectFromProjectItem(selected.ProjectItem, selectedFilePaths);
                }
            }

            if (targetProject == null)
            {
                VsShellUtilities.ShowMessageBox(
                    _package, "No project found in selection.",
                    "Extract Interfaces", OLEMSGICON.OLEMSGICON_INFO,
                    OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                return;
            }

            // Scan the project for all extractable classes
            var allClasses = await ScanProjectForClassesAsync(targetProject);

            if (allClasses.Count == 0)
            {
                VsShellUtilities.ShowMessageBox(
                    _package, "No public, non-static classes found in this project.",
                    "Extract Interfaces", OLEMSGICON.OLEMSGICON_INFO,
                    OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                return;
            }

            // Defaults from the first pre-selected class (or first class in project)
            var firstSelected = allClasses.FirstOrDefault(c => selectedFilePaths.Contains(c.FilePath))
                             ?? allClasses[0];

            var defaultNamespace = firstSelected.Namespace ?? string.Empty;
            var defaultFolder    = Path.GetDirectoryName(firstSelected.FilePath) ?? string.Empty;

            // Show dialog
            var dialog = new ExtractInterfacesDialog(allClasses, selectedFilePaths, defaultNamespace, defaultFolder);
            if (dialog.ShowModal() != true || dialog.Result == null) return;

            // Run extraction
            DevUtilsPane.Log($"[Dev Utils] Batch Extract Interfaces — started ({dialog.Result.SelectedClasses.Count} class(es))");
            DevUtilsPane.Activate();

            Action<string> log = msg => DevUtilsPane.Log(msg);
            var service = new InterfaceExtractorService();
            var (created, skipped) = await service.ExtractBatchAsync(dialog.Result, log);

            DevUtilsPane.Log($"[Dev Utils] Batch Extract Interfaces — done. Created: {created}, Skipped: {skipped}");

            VsShellUtilities.ShowMessageBox(
                _package,
                $"Done!\n\nInterfaces created: {created}\nSkipped (already exist): {skipped}",
                "Extract Interfaces in Batch",
                OLEMSGICON.OLEMSGICON_INFO,
                OLEMSGBUTTON.OLEMSGBUTTON_OK,
                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        }

        // ── Project scanning ─────────────────────────────────────────────────────

        private async Task<List<ClassInfo>> ScanProjectForClassesAsync(Project project)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var projectDir = Path.GetDirectoryName(project.FullName) ?? string.Empty;
            var items      = new List<(string path, ProjectItem item)>();
            CollectAllCsItems(project.ProjectItems, items);

            var result = new List<ClassInfo>();
            foreach (var (filePath, projectItem) in items)
            {
                var relativeFolderPath = GetRelativeFolderPath(projectDir, filePath);
                var classesInFile      = await ParseFileForClassesAsync(filePath, relativeFolderPath, projectItem);
                result.AddRange(classesInFile);
            }
            return result;
        }

        private static async System.Threading.Tasks.Task<List<ClassInfo>> ParseFileForClassesAsync(
            string filePath, string relativeFolderPath, ProjectItem projectItem)
        {
            var code = File.ReadAllText(filePath, Encoding.UTF8);
            var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(code);
            var root = await tree.GetRootAsync() as CompilationUnitSyntax;
            if (root == null) return new List<ClassInfo>();

            return root.DescendantNodes()
                .OfType<ClassDeclarationSyntax>()
                .Where(c => c.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword))
                         && !c.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)
                                              || m.IsKind(SyntaxKind.AbstractKeyword)))
                .Select(c => new ClassInfo
                {
                    ClassName          = c.Identifier.Text,
                    FilePath           = filePath,
                    RelativeFolderPath = relativeFolderPath,
                    Namespace          = GetNamespace(c),
                    ProjectItem        = projectItem,
                })
                .ToList();
        }

        // ── DTE helpers ──────────────────────────────────────────────────────────

        private void CollectAllCsItems(ProjectItems items, List<(string, ProjectItem)> result)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (items == null) return;
            foreach (ProjectItem item in items)
            {
                if (item.Kind == EnvDTE.Constants.vsProjectItemKindPhysicalFile)
                {
                    var name = item.Name;
                    if (name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                        && !IsIgnored(name)
                        && !IsInBinOrObj(item.FileNames[1]))
                    {
                        result.Add((item.FileNames[1], item));
                    }
                }
                CollectAllCsItems(item.ProjectItems, result);
            }
        }

        private void CollectFromProjectItem(ProjectItem item, HashSet<string> paths)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (item.Kind == EnvDTE.Constants.vsProjectItemKindPhysicalFile)
            {
                var name = item.Name;
                if (name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                    && !IsIgnored(name) && !IsInBinOrObj(item.FileNames[1]))
                    paths.Add(item.FileNames[1]);
            }
            if (item.ProjectItems != null)
                CollectCsFilePaths(item.ProjectItems, paths);
        }

        private void CollectCsFilePaths(ProjectItems items, HashSet<string> paths)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (items == null) return;
            foreach (ProjectItem item in items)
                CollectFromProjectItem(item, paths);
        }

        // ── Static helpers ───────────────────────────────────────────────────────

        private static string GetRelativeFolderPath(string projectDir, string filePath)
        {
            try
            {
                var baseUri   = new Uri(projectDir.TrimEnd('\\', '/') + "\\");
                var targetUri = new Uri(filePath);
                var relative  = Uri.UnescapeDataString(baseUri.MakeRelativeUri(targetUri).ToString())
                                   .Replace('/', '\\');
                return Path.GetDirectoryName(relative) ?? string.Empty;
            }
            catch { return string.Empty; }
        }

        private static string GetNamespace(ClassDeclarationSyntax classDecl)
        {
            foreach (var ancestor in classDecl.Ancestors())
            {
                if (ancestor is NamespaceDeclarationSyntax ns)
                    return ns.Name.ToString();
                if (ancestor is FileScopedNamespaceDeclarationSyntax fns)
                    return fns.Name.ToString();
            }
            return null;
        }

        private static bool IsIgnored(string fileName)
            => IgnoredFileNames.Contains(fileName)
            || fileName.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase);

        private static bool IsInBinOrObj(string path)
            => path.IndexOf(@"\bin\", StringComparison.OrdinalIgnoreCase) >= 0
            || path.IndexOf(@"\obj\", StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
