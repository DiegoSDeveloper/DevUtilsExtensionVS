using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using EnvDTE;
using DevUtils.ExtensionVS.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.VisualStudio.Shell;

namespace DevUtils.ExtensionVS.Services
{
    internal class DiAnalyzerService
    {
        private static readonly HashSet<string> PrimitiveTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "string","String","int","Int32","long","Int64","bool","Boolean",
            "double","Double","float","Single","decimal","Decimal",
            "byte","Byte","char","Char","short","Int16","uint","UInt32",
            "ulong","UInt64","ushort","UInt16","sbyte","SByte","object","Object",
            "Guid","DateTime","TimeSpan","Uri"
        };

        // ── Public entry point ────────────────────────────────────────────────

        public async Task<(List<DiRegistration> registrations, List<InjectionAnalysis> analyses)>
            AnalyzeAsync(
                string baseDir,
                List<(string path, ProjectItem item)> registrationFiles,
                List<(string path, ProjectItem item)> allSolutionFiles)
        {
            Debug.WriteLine($"[DiAnalyzer] AnalyzeAsync started. Registration files: {registrationFiles.Count}, Solution files: {allSolutionFiles.Count}");
            foreach (var f in registrationFiles)
                Debug.WriteLine($"[DiAnalyzer]   RegFile: {f.path}");

            // 1 — scan DI registrations only from the selected project files
            var registrations = await ScanDiRegistrationsAsync(registrationFiles);
            Debug.WriteLine($"[DiAnalyzer] Registrations found: {registrations.Count}");
            foreach (var r in registrations)
                Debug.WriteLine($"[DiAnalyzer]   {r.InterfaceName} -> {r.ClassName}");

            if (registrations.Count == 0)
            {
                Debug.WriteLine("[DiAnalyzer] No registrations found, returning empty.");
                return (registrations, new List<InjectionAnalysis>());
            }

            var registeredInterfaces = new HashSet<string>(registrations.Select(r => r.InterfaceName), StringComparer.Ordinal);

            // 2 — resolve interface namespaces by scanning solution files
            await FillInterfaceNamespacesAsync(registrations, allSolutionFiles);
            foreach (var r in registrations)
                Debug.WriteLine($"[DiAnalyzer]   {r.InterfaceName} namespace: '{r.InterfaceNamespace}'");

            // 3 — build per-file "new ClassName(" cache across the whole solution
            var newUsageMap = BuildNewUsageMap(allSolutionFiles.Select(f => f.path));
            Debug.WriteLine($"[DiAnalyzer] newUsageMap entries: {newUsageMap.Count}");

            // 3 — scan ALL public non-static classes across the whole solution as injection targets
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            Debug.WriteLine($"[DiAnalyzer] Base dir: {baseDir}");
            var analyses = new List<InjectionAnalysis>();

            foreach (var (path, item) in allSolutionFiles)
            {
                Debug.WriteLine($"[DiAnalyzer] Scanning file: {path}");

                string code;
                try { code = File.ReadAllText(path, Encoding.UTF8); }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[DiAnalyzer]   SKIP (read error): {ex.Message}");
                    continue;
                }

                var tree = CSharpSyntaxTree.ParseText(code);
                var root = await tree.GetRootAsync() as CompilationUnitSyntax;
                if (root == null)
                {
                    Debug.WriteLine($"[DiAnalyzer]   SKIP (root is null)");
                    continue;
                }

                var classDecls = root.DescendantNodes()
                    .OfType<ClassDeclarationSyntax>()
                    .Where(c => c.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword))
                             && !c.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword))
                             && !c.Modifiers.Any(m => m.IsKind(SyntaxKind.AbstractKeyword)))
                    .ToList();

                Debug.WriteLine($"[DiAnalyzer]   Public non-static classes found: {classDecls.Count}");

                if (classDecls.Count == 0) continue;

                var folder = GetRelativeFolderPath(baseDir, path);

                foreach (var classDecl in classDecls)
                {
                    var className = classDecl.Identifier.Text;
                    Debug.WriteLine($"[DiAnalyzer]   Class: {className}");

                    // Check at the class body level (not the whole file) to avoid false positives
                    // when multiple classes share a file (e.g. DTO + Controller in same file)
                    var classText = classDecl.ToFullString();

                    foreach (var ifaceReg in registrations)
                    {
                        if (string.Equals(className, ifaceReg.ClassName, StringComparison.Ordinal))
                        {
                            Debug.WriteLine($"[DiAnalyzer]     SKIP self: {className} is impl of {ifaceReg.InterfaceName}");
                            continue;
                        }

                        // Word-boundary check inside the class body only
                        // Prevents "IProduto" matching inside "IProdutoRepositorio"
                        if (!ContainsWord(classText, ifaceReg.ClassName) && !ContainsWord(classText, ifaceReg.InterfaceName))
                        {
                            Debug.WriteLine($"[DiAnalyzer]     SKIP no-ref: {className} has no word-match for {ifaceReg.ClassName}/{ifaceReg.InterfaceName}");
                            continue;
                        }

                        var analysis = AnalyzeOne(
                            classDecl, className, path, folder,
                            item, ifaceReg, registeredInterfaces, newUsageMap);

                        Debug.WriteLine($"[DiAnalyzer]     Pair ({className}, {ifaceReg.InterfaceName}) -> {analysis.Status}: {analysis.Reason}");
                        analyses.Add(analysis);
                    }
                }
            }

            Debug.WriteLine($"[DiAnalyzer] Total analyses produced: {analyses.Count}");

            return (registrations, analyses
                .OrderBy(a => a.FolderPath)
                .ThenBy(a => a.TargetClassName)
                .ThenBy(a => a.InterfaceName)
                .ToList());
        }

        // ── DI registration scanner ───────────────────────────────────────────

        public static async Task<List<DiRegistration>> ScanDiRegistrationsAsync(
            IEnumerable<(string path, ProjectItem item)> files)
        {
            var result = new List<DiRegistration>();

            foreach (var (path, _) in files)
            {
                try
                {
                    var code = File.ReadAllText(path, Encoding.UTF8);
                    if (!code.Contains("AddScoped") && !code.Contains("AddTransient")
                        && !code.Contains("AddSingleton")) continue;

                    Debug.WriteLine($"[DiAnalyzer] ScanDI: checking {Path.GetFileName(path)}");

                    var tree = CSharpSyntaxTree.ParseText(code);
                    var root = await tree.GetRootAsync() as CompilationUnitSyntax;
                    if (root == null) continue;

                    foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                    {
                        if (!(inv.Expression is MemberAccessExpressionSyntax ma)) continue;
                        if (!(ma.Name is GenericNameSyntax gn)) continue;

                        var method = gn.Identifier.Text;
                        if (method != "AddScoped" && method != "AddTransient" && method != "AddSingleton"
                            && method != "TryAddScoped" && method != "TryAddTransient" && method != "TryAddSingleton")
                            continue;

                        var args = gn.TypeArgumentList?.Arguments;
                        if (args == null || args.Value.Count != 2)
                        {
                            Debug.WriteLine($"[DiAnalyzer] {method} found but arg count != 2 (count={args?.Count})");
                            continue;
                        }

                        var iface = args.Value[0].ToString().Split('.').Last();
                        var impl  = args.Value[1].ToString().Split('.').Last();

                        Debug.WriteLine($"[DiAnalyzer]   {method}<{iface},{impl}> — IsInterface={IsInterfaceName(iface)}");

                        if (IsInterfaceName(iface) && !string.IsNullOrEmpty(impl))
                        {
                            result.Add(new DiRegistration
                            {
                                InterfaceName = iface,
                                ClassName     = impl,
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[DiAnalyzer] ScanDI exception on {path}: {ex.Message}");
                }
            }

            var final = result.GroupBy(r => r.InterfaceName).Select(g => g.First()).ToList();
            Debug.WriteLine($"[DiAnalyzer] ScanDI result (deduplicated): {final.Count}");
            return final;
        }

        // ── Analysis for one (targetClass, interface) pair ────────────────────

        private static InjectionAnalysis AnalyzeOne(
            ClassDeclarationSyntax classDecl,
            string className, string filePath, string folder,
            ProjectItem item, DiRegistration ifaceReg,
            HashSet<string> registeredInterfaces,
            Dictionary<string, HashSet<string>> newUsageMap)
        {
            var base_ = new InjectionAnalysis
            {
                TargetClassName         = className,
                TargetFilePath          = filePath,
                FolderPath              = string.IsNullOrEmpty(folder) ? "(root)" : folder,
                TargetProjectItem       = item,
                InterfaceName           = ifaceReg.InterfaceName,
                InterfaceNamespace      = ifaceReg.InterfaceNamespace,
                ImplementationClassName = ifaceReg.ClassName,
            };

            var ctors = classDecl.Members
                .OfType<ConstructorDeclarationSyntax>()
                .Where(c => c.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)))
                .ToList();

            base_.ConstructorCount = ctors.Count;

            // Already injected?
            if (ctors.Any(c => c.ParameterList.Parameters
                    .Any(p => ShortTypeName(p.Type) == ifaceReg.InterfaceName)))
            {
                base_.Status = InjectionStatus.AlreadyDone;
                base_.Reason = "Already in constructor";
                return base_;
            }

            // ── Error checks ──────────────────────────────────────────────────

            if (classDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)))
            {
                base_.Status = InjectionStatus.Error;
                base_.Reason = "Static class cannot receive injection";
                return base_;
            }

            if (ctors.Count == 0)
            {
                base_.Status = InjectionStatus.Safe;
                base_.Reason = "No public constructor — one will be created during injection";
                return base_;
            }

            // new ClassName( found in project (outside test files)
            if (newUsageMap.TryGetValue(className, out var usageFiles))
            {
                var prodFiles = usageFiles
                        .Where(f => !f.ToLower().Contains("test"))
                        .ToList();
                if (prodFiles.Count > 0)
                {
                    var shortName = Path.GetFileName(prodFiles[0]);
                    base_.Status = InjectionStatus.Error;
                    base_.Reason = $"Direct 'new {className}()' found in {shortName}";
                    return base_;
                }
            }

            // Primitive params without defaults
            var allParams = ctors.SelectMany(c => c.ParameterList.Parameters).ToList();
            var badParams = allParams
                .Where(p => PrimitiveTypes.Contains(ShortTypeName(p.Type)) && p.Default == null)
                .ToList();

            if (badParams.Count > 0)
            {
                base_.Status = InjectionStatus.Error;
                base_.Reason = $"Primitive parameter(s) without defaults: {string.Join(", ", badParams.Select(p => p.Type?.ToString()))}";
                return base_;
            }

            // ── Warning / Safe ────────────────────────────────────────────────

            bool allDiCompatible = allParams.All(p =>
            {
                var t = ShortTypeName(p.Type);
                return IsInterfaceName(t) || registeredInterfaces.Contains(t) || p.Default != null;
            });

            if (ctors.Count > 1)
            {
                base_.Status = InjectionStatus.Warning;
                base_.Reason = allDiCompatible
                    ? $"{ctors.Count} constructors — all DI-compatible, will inject into all"
                    : $"{ctors.Count} constructors — some params may not be DI-resolvable";
                return base_;
            }

            base_.Status = InjectionStatus.Safe;
            base_.Reason = "1 constructor, all existing params are DI-compatible";
            return base_;
        }

        // ── Interface namespace resolver ──────────────────────────────────────

        private static async Task FillInterfaceNamespacesAsync(
            List<DiRegistration> registrations,
            IEnumerable<(string path, ProjectItem item)> files)
        {
            var needed = new HashSet<string>(
                registrations.Where(r => string.IsNullOrEmpty(r.InterfaceNamespace))
                             .Select(r => r.InterfaceName),
                StringComparer.Ordinal);

            foreach (var (path, _) in files)
            {
                if (needed.Count == 0) break;
                try
                {
                    var code = File.ReadAllText(path, Encoding.UTF8);
                    if (!needed.Any(n => code.Contains(n))) continue;

                    var tree = CSharpSyntaxTree.ParseText(code);
                    var root = await tree.GetRootAsync() as CompilationUnitSyntax;
                    if (root == null) continue;

                    foreach (var iface in root.DescendantNodes().OfType<InterfaceDeclarationSyntax>())
                    {
                        if (!needed.Contains(iface.Identifier.Text)) continue;

                        var ns = iface.Ancestors()
                            .OfType<NamespaceDeclarationSyntax>()
                            .FirstOrDefault()?.Name.ToString() ?? string.Empty;

                        foreach (var reg in registrations.Where(r => r.InterfaceName == iface.Identifier.Text))
                            reg.InterfaceNamespace = ns;

                        needed.Remove(iface.Identifier.Text);
                    }
                }
                catch { }
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static Dictionary<string, HashSet<string>> BuildNewUsageMap(IEnumerable<string> filePaths)
        {
            var map = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

            foreach (var path in filePaths)
            {
                try
                {
                    var code = File.ReadAllText(path, Encoding.UTF8);
                    var matches = Regex.Matches(code, @"\bnew\s+([A-Z][A-Za-z0-9_]*)\s*\(");
                    foreach (Match m in matches)
                    {
                        var name = m.Groups[1].Value;
                        if (!map.TryGetValue(name, out var set))
                            map[name] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        set.Add(path);
                    }
                }
                catch { /* skip */ }
            }

            return map;
        }

        private static string ShortTypeName(Microsoft.CodeAnalysis.CSharp.Syntax.TypeSyntax type)
            => type?.ToString().Split('.').Last() ?? string.Empty;

        private static bool IsInterfaceName(string name)
            => name.Length > 1 && name[0] == 'I' && char.IsUpper(name[1]);

        private static bool ContainsWord(string text, string word)
        {
            if (string.IsNullOrEmpty(word)) return false;
            var idx = 0;
            while ((idx = text.IndexOf(word, idx, StringComparison.Ordinal)) >= 0)
            {
                var prevOk = idx == 0 || !IsIdentChar(text[idx - 1]);
                var nextOk = idx + word.Length >= text.Length || !IsIdentChar(text[idx + word.Length]);
                if (prevOk && nextOk) return true;
                idx += word.Length;
            }
            return false;
        }

        private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_';

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
    }
}
