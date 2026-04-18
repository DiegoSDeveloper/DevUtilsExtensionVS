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
    internal sealed class RegisterDiCommand
    {
        public const int CommandId = 0x0101;
        public static readonly Guid CommandSet = new Guid("a3b4c5d6-e7f8-4901-a2b3-c4d5e6f70001");

        private readonly AsyncPackage _package;

        private RegisterDiCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            _package = package;
            var cmdId = new CommandID(CommandSet, CommandId);
            commandService.AddCommand(new OleMenuCommand(Execute, cmdId));
        }

        public static RegisterDiCommand Instance { get; private set; }

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            Instance = new RegisterDiCommand(package, commandService);
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
                    "Generate DI Registration", OLEMSGICON.OLEMSGICON_INFO,
                    OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                return;
            }

            var registrations = await ScanProjectForRegistrationsAsync(targetProject);

            if (registrations.Count == 0)
            {
                VsShellUtilities.ShowMessageBox(
                    _package, "No interface → class pairs found in this project.\n\nExtract interfaces first using 'Batch Extract Interfaces'.",
                    "Generate DI Registration", OLEMSGICON.OLEMSGICON_INFO,
                    OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                return;
            }

            var projectName    = Path.GetFileNameWithoutExtension(targetProject.FullName);
            var projectDir     = Path.GetDirectoryName(targetProject.FullName) ?? string.Empty;
            var defaultNs      = registrations.Select(r => r.ClassNamespace)
                                              .Where(n => !string.IsNullOrEmpty(n))
                                              .GroupBy(n => n)
                                              .OrderByDescending(g => g.Count())
                                              .FirstOrDefault()?.Key ?? projectName;
            var defaultMethod  = "Add" + ToPascalWord(projectName.Split('.').Last());

            var dialog = new RegisterDiDialog(
                registrations,
                defaultClassName:    "DependencyInjection",
                defaultMethodName:   defaultMethod,
                defaultNamespace:    defaultNs,
                defaultOutputFolder: projectDir);

            if (dialog.ShowModal() != true || dialog.Result == null) return;

            DevUtilsPane.Log($"[Dev Utils] Generate DI Registration — started ({dialog.Result.Registrations.Count} registration(s))");
            DevUtilsPane.Activate();

            Action<string> log = msg => DevUtilsPane.Log(msg);
            var service  = new DiRegistrationService();
            var filePath = service.Generate(dialog.Result, targetProject, log);

            DevUtilsPane.Log($"[Dev Utils] Generate DI Registration — done.");

            VsShellUtilities.ShowMessageBox(
                _package,
                $"File generated successfully:\n{filePath}",
                "Generate DI Registration",
                OLEMSGICON.OLEMSGICON_INFO,
                OLEMSGBUTTON.OLEMSGBUTTON_OK,
                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        }

        // ── Scanning ─────────────────────────────────────────────────────────────

        private async Task<List<DiRegistration>> ScanProjectForRegistrationsAsync(Project project)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var projectDir = Path.GetDirectoryName(project.FullName) ?? string.Empty;
            var allFiles   = new List<(string path, ProjectItem item)>();
            CollectCsFiles(project.ProjectItems, allFiles);

            // Pass 1: build interface → namespace lookup
            var ifaceNamespaces = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (path, _) in allFiles)
                await CollectInterfaceNamespacesAsync(path, ifaceNamespaces);

            // Pass 2: find class → interface pairs
            var result = new List<DiRegistration>();
            foreach (var (path, item) in allFiles)
            {
                var folder = GetRelativeFolderPath(projectDir, path);
                await CollectRegistrationsFromFileAsync(path, folder, ifaceNamespaces, result);
            }

            return result
                .GroupBy(r => $"{r.InterfaceName}|{r.ClassName}")
                .Select(g => g.First())
                .OrderBy(r => r.FolderPath)
                .ThenBy(r => r.InterfaceName)
                .ToList();
        }

        private static async Task CollectInterfaceNamespacesAsync(
            string filePath,
            Dictionary<string, string> lookup)
        {
            try
            {
                var code = File.ReadAllText(filePath, Encoding.UTF8);
                var tree = CSharpSyntaxTree.ParseText(code);
                var root = await tree.GetRootAsync() as CompilationUnitSyntax;
                if (root == null) return;

                foreach (var iface in root.DescendantNodes().OfType<InterfaceDeclarationSyntax>())
                {
                    var ns = GetNamespace(iface);
                    lookup[iface.Identifier.Text] = ns ?? string.Empty;
                }
            }
            catch { /* skip unreadable files */ }
        }

        private static async Task CollectRegistrationsFromFileAsync(
            string filePath,
            string folderPath,
            Dictionary<string, string> ifaceNamespaces,
            List<DiRegistration> result)
        {
            try
            {
                var code = File.ReadAllText(filePath, Encoding.UTF8);
                var tree = CSharpSyntaxTree.ParseText(code);
                var root = await tree.GetRootAsync() as CompilationUnitSyntax;
                if (root == null) return;

                var classes = root.DescendantNodes()
                    .OfType<ClassDeclarationSyntax>()
                    .Where(c => c.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword))
                             && !c.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)
                                                  || m.IsKind(SyntaxKind.AbstractKeyword))
                             && c.BaseList != null);

                foreach (var cls in classes)
                {
                    var classNs = GetNamespace(cls);

                    foreach (var baseType in cls.BaseList.Types)
                    {
                        var typeName = baseType.Type.ToString().Split('.').Last();
                        if (!IsInterfaceName(typeName)) continue;

                        ifaceNamespaces.TryGetValue(typeName, out var ifaceNs);

                        result.Add(new DiRegistration
                        {
                            InterfaceName      = typeName,
                            ClassName          = cls.Identifier.Text,
                            InterfaceNamespace = ifaceNs ?? classNs,
                            ClassNamespace     = classNs,
                            FolderPath         = string.IsNullOrEmpty(folderPath) ? "(root)" : folderPath,
                        });
                    }
                }
            }
            catch { /* skip unreadable files */ }
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        private void CollectCsFiles(ProjectItems items, List<(string, ProjectItem)> result)
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
                CollectCsFiles(item.ProjectItems, result);
            }
        }

        private static bool IsInterfaceName(string name)
            => name.Length > 1 && name[0] == 'I' && char.IsUpper(name[1]);

        private static string GetNamespace(SyntaxNode node)
        {
            foreach (var ancestor in node.Ancestors())
            {
                if (ancestor is NamespaceDeclarationSyntax ns)  return ns.Name.ToString();
                if (ancestor is FileScopedNamespaceDeclarationSyntax fns) return fns.Name.ToString();
            }
            return null;
        }

        private static string GetRelativeFolderPath(string projectDir, string filePath)
        {
            try
            {
                var baseUri   = new Uri(projectDir.TrimEnd('\\', '/') + "\\");
                var targetUri = new Uri(filePath);
                var rel       = Uri.UnescapeDataString(baseUri.MakeRelativeUri(targetUri).ToString())
                                   .Replace('/', '\\');
                return Path.GetDirectoryName(rel) ?? string.Empty;
            }
            catch { return string.Empty; }
        }

        private static string ToPascalWord(string s)
            => string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s.Substring(1);
    }
}
