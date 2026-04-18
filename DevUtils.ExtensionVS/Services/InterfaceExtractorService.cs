using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DevUtils.ExtensionVS.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.VisualStudio.Shell;

namespace DevUtils.ExtensionVS.Services
{
    internal class InterfaceExtractorService
    {
        public async Task<(int created, int skipped)> ExtractBatchAsync(
            ExtractionOptions options, Action<string> log = null)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            int created = 0, skipped = 0;

            // Group by source file so we write each file only once
            var byFile = options.SelectedClasses.GroupBy(c => c.FilePath);

            foreach (var fileGroup in byFile)
            {
                var (c, s) = await ProcessFileAsync(
                    fileGroup.Key,
                    fileGroup.ToList(),
                    options.NamespaceOverride,
                    options.OutputFolderOverride,
                    log);

                created += c;
                skipped += s;
            }

            return (created, skipped);
        }

        private static async Task<(int created, int skipped)> ProcessFileAsync(
            string filePath,
            IReadOnlyList<ClassInfo> classInfos,
            string namespaceOverride,
            string outputFolderOverride,
            Action<string> log)
        {
            var originalCode = File.ReadAllText(filePath, Encoding.UTF8);
            var tree         = CSharpSyntaxTree.ParseText(originalCode);
            var root         = (CompilationUnitSyntax)await tree.GetRootAsync();

            int created = 0, skipped = 0;
            var classesToUpdate = new List<(string className, string interfaceName)>();

            foreach (var classInfo in classInfos)
            {
                var className     = classInfo.ClassName;
                var interfaceName = "I" + className;
                var outputDir     = string.IsNullOrWhiteSpace(outputFolderOverride)
                                        ? Path.GetDirectoryName(filePath)
                                        : outputFolderOverride;
                var interfaceFile = Path.Combine(outputDir, interfaceName + ".cs");

                if (File.Exists(interfaceFile))
                {
                    log?.Invoke($"  [skipped]  {interfaceFile}");
                    skipped++;
                    continue;
                }

                // Find the class declaration in the parsed root
                var classDecl = root.DescendantNodes()
                    .OfType<ClassDeclarationSyntax>()
                    .FirstOrDefault(c => c.Identifier.Text == className
                        && c.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword))
                        && !c.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)
                                             || m.IsKind(SyntaxKind.AbstractKeyword)));

                if (classDecl == null) continue;

                var publicMethods = classDecl.Members
                    .OfType<MethodDeclarationSyntax>()
                    .Where(m => m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.PublicKeyword))
                             && !m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.StaticKeyword)));

                var publicProps = classDecl.Members
                    .OfType<PropertyDeclarationSyntax>()
                    .Where(p => p.Modifiers.Any(mod => mod.IsKind(SyntaxKind.PublicKeyword))
                             && !p.Modifiers.Any(mod => mod.IsKind(SyntaxKind.StaticKeyword)));

                if (!publicMethods.Any() && !publicProps.Any()) continue;

                var members = new List<MemberDeclarationSyntax>();
                foreach (var prop in publicProps)   members.Add(BuildInterfaceProperty(prop));
                foreach (var method in publicMethods) members.Add(BuildInterfaceMethod(method));

                var interfaceDecl = SyntaxFactory.InterfaceDeclaration(interfaceName)
                    .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
                    .AddMembers(members.ToArray());

                // Use namespace override, or fall back to the class's own namespace
                var namespaceName = !string.IsNullOrWhiteSpace(namespaceOverride)
                    ? namespaceOverride
                    : classInfo.Namespace;

                CompilationUnitSyntax interfaceUnit;
                if (!string.IsNullOrWhiteSpace(namespaceName))
                {
                    var ns = SyntaxFactory
                        .NamespaceDeclaration(SyntaxFactory.ParseName(namespaceName))
                        .AddMembers(interfaceDecl);
                    interfaceUnit = SyntaxFactory.CompilationUnit()
                        .AddUsings(root.Usings.ToArray())
                        .AddMembers(ns);
                }
                else
                {
                    interfaceUnit = SyntaxFactory.CompilationUnit()
                        .AddUsings(root.Usings.ToArray())
                        .AddMembers(interfaceDecl);
                }

                Directory.CreateDirectory(outputDir);
                File.WriteAllText(interfaceFile, interfaceUnit.NormalizeWhitespace().ToFullString(), Encoding.UTF8);

                log?.Invoke($"  [created]  {interfaceFile}");

                // Add the new file to the VS project (requires main thread)
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                classInfo.ProjectItem.Collection.AddFromFile(interfaceFile);

                // Use fully qualified name in the base list when the interface lives in a different namespace
                var interfaceRef = !string.IsNullOrWhiteSpace(namespaceOverride)
                    && !string.IsNullOrWhiteSpace(classInfo.Namespace)
                    && namespaceOverride != classInfo.Namespace
                        ? $"{namespaceOverride}.{interfaceName}"
                        : interfaceName;

                classesToUpdate.Add((className, interfaceRef));
                created++;
            }

            // Patch the source file once for all updated classes
            if (classesToUpdate.Count > 0)
            {
                var updatedRoot = root;
                foreach (var (className, interfaceName) in classesToUpdate)
                    updatedRoot = AddInterfaceToClass(updatedRoot, className, interfaceName);

                File.WriteAllText(filePath, updatedRoot.ToFullString(), Encoding.UTF8);
            }

            return (created, skipped);
        }

        // ── Roslyn helpers ───────────────────────────────────────────────────────

        private static CompilationUnitSyntax AddInterfaceToClass(
            CompilationUnitSyntax root, string className, string interfaceName)
        {
            var target = root.DescendantNodes()
                .OfType<ClassDeclarationSyntax>()
                .FirstOrDefault(c => c.Identifier.Text == className);

            if (target == null) return root;

            var alreadyImplements = target.BaseList?.Types
                .Any(t => t.Type.ToString() == interfaceName) ?? false;
            if (alreadyImplements) return root;

            var ifaceType = SyntaxFactory.SimpleBaseType(SyntaxFactory.ParseTypeName(interfaceName));

            ClassDeclarationSyntax updated;
            if (target.BaseList == null)
            {
                updated = target.WithBaseList(
                    SyntaxFactory.BaseList(
                        SyntaxFactory.Token(SyntaxKind.ColonToken)
                            .WithLeadingTrivia(SyntaxFactory.Space)
                            .WithTrailingTrivia(SyntaxFactory.Space),
                        SyntaxFactory.SingletonSeparatedList<BaseTypeSyntax>(ifaceType)));
            }
            else
            {
                updated = target.WithBaseList(target.BaseList.AddTypes(ifaceType));
            }

            return root.ReplaceNode(target, updated);
        }

        private static PropertyDeclarationSyntax BuildInterfaceProperty(PropertyDeclarationSyntax prop)
        {
            var accessors = new List<AccessorDeclarationSyntax>();
            if (prop.AccessorList != null)
            {
                foreach (var acc in prop.AccessorList.Accessors)
                {
                    if (acc.Modifiers.Any()) continue; // skip private/protected accessors
                    var kind = acc.Kind();
                    if (kind == SyntaxKind.GetAccessorDeclaration
                        || kind == SyntaxKind.SetAccessorDeclaration
                        || kind == SyntaxKind.InitAccessorDeclaration)
                    {
                        accessors.Add(SyntaxFactory.AccessorDeclaration(kind)
                            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));
                    }
                }
            }
            return SyntaxFactory.PropertyDeclaration(prop.Type.WithoutTrivia(), prop.Identifier)
                .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(accessors)));
        }

        private static MethodDeclarationSyntax BuildInterfaceMethod(MethodDeclarationSyntax method)
        {
            return SyntaxFactory.MethodDeclaration(method.ReturnType.WithoutTrivia(), method.Identifier)
                .WithTypeParameterList(method.TypeParameterList)
                .WithParameterList(method.ParameterList)
                .WithConstraintClauses(method.ConstraintClauses)
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
        }
    }
}
