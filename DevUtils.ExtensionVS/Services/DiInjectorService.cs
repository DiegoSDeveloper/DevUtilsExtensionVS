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

namespace DevUtils.ExtensionVS.Services
{
    internal class DiInjectorService
    {
        public async Task<bool> InjectAsync(InjectionAnalysis analysis, Action<string> log = null)
        {
            if (analysis.Status != InjectionStatus.Safe && analysis.Status != InjectionStatus.Warning)
                return false;

            var code = File.ReadAllText(analysis.TargetFilePath, Encoding.UTF8);
            var tree = CSharpSyntaxTree.ParseText(code);
            var root = (CompilationUnitSyntax)await tree.GetRootAsync();

            var classDecl = root.DescendantNodes()
                .OfType<ClassDeclarationSyntax>()
                .FirstOrDefault(c => c.Identifier.Text == analysis.TargetClassName);

            if (classDecl == null) return false;

            var ctors = classDecl.Members
                .OfType<ConstructorDeclarationSyntax>()
                .Where(c => c.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)))
                .ToList();

            var interfaceName = analysis.InterfaceName;
            var fieldName     = ToFieldName(interfaceName);
            var paramName     = ToParamName(interfaceName);

            // Use fully qualified name to avoid any namespace ambiguity
            var qualifiedName = !string.IsNullOrWhiteSpace(analysis.InterfaceNamespace)
                ? analysis.InterfaceNamespace + "." + interfaceName
                : interfaceName;

            var updatedClass = classDecl;
            var field = BuildField(qualifiedName, fieldName);

            if (ctors.Count > 0)
            {
                // Insert field just before the first existing constructor
                var firstCtorIdx = updatedClass.Members
                    .Select((m, i) => new { m, i })
                    .First(x => x.m is ConstructorDeclarationSyntax).i;

                updatedClass = updatedClass.WithMembers(
                    updatedClass.Members.Insert(firstCtorIdx, field));

                updatedClass = UpdateConstructors(updatedClass, interfaceName, qualifiedName, fieldName, paramName);
            }
            else
            {
                // Insert field + new constructor before first non-field member (after existing fields)
                var insertIdx = updatedClass.Members
                    .Select((m, i) => new { m, i })
                    .FirstOrDefault(x => !(x.m is FieldDeclarationSyntax))?.i
                    ?? updatedClass.Members.Count;

                var newCtor = BuildConstructor(classDecl.Identifier.Text, qualifiedName, fieldName, paramName);

                updatedClass = updatedClass.WithMembers(
                    updatedClass.Members.Insert(insertIdx, field));
                updatedClass = updatedClass.WithMembers(
                    updatedClass.Members.Insert(insertIdx + 1, newCtor));
            }

            // Replace direct "new ImplementationClass(...)" usages with the field reference
            if (!string.IsNullOrEmpty(analysis.ImplementationClassName))
                updatedClass = ReplaceDirectInstantiations(updatedClass, analysis.ImplementationClassName, fieldName);

            var updatedRoot = root.ReplaceNode(classDecl, updatedClass);

            File.WriteAllText(analysis.TargetFilePath, updatedRoot.ToFullString(), Encoding.UTF8);
            log?.Invoke($"  [injected] {analysis.InterfaceName} → {analysis.TargetClassName}  ({analysis.TargetFilePath})");
            return true;
        }

        // ── Build constructor (when none exists) ──────────────────────────────

        private static ConstructorDeclarationSyntax BuildConstructor(
            string className, string qualifiedName, string fieldName, string paramName)
        {
            var assignment = SyntaxFactory.ExpressionStatement(
                SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    SyntaxFactory.IdentifierName(fieldName),
                    SyntaxFactory.IdentifierName(paramName)))
                .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed);

            return SyntaxFactory.ConstructorDeclaration(className)
                .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword)
                    .WithTrailingTrivia(SyntaxFactory.Space))
                .AddParameterListParameters(
                    SyntaxFactory.Parameter(SyntaxFactory.Identifier(paramName))
                        .WithType(SyntaxFactory.ParseTypeName(qualifiedName)
                            .WithTrailingTrivia(SyntaxFactory.Space)))
                .WithBody(SyntaxFactory.Block(assignment))
                .WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed)
                .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed);
        }

        // ── Build field ───────────────────────────────────────────────────────

        private static FieldDeclarationSyntax BuildField(string qualifiedName, string fieldName)
        {
            return SyntaxFactory.FieldDeclaration(
                    SyntaxFactory.VariableDeclaration(
                        SyntaxFactory.ParseTypeName(qualifiedName)
                            .WithTrailingTrivia(SyntaxFactory.Whitespace(" ")))
                    .AddVariables(SyntaxFactory.VariableDeclarator(fieldName)))
                .AddModifiers(
                    SyntaxFactory.Token(SyntaxKind.PrivateKeyword)
                        .WithTrailingTrivia(SyntaxFactory.Space),
                    SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword)
                        .WithTrailingTrivia(SyntaxFactory.Space))
                .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed);
        }

        // ── Replace direct new ClassName() with field reference ──────────────

        private static ClassDeclarationSyntax ReplaceDirectInstantiations(
            ClassDeclarationSyntax classDecl,
            string implClassName,
            string fieldName)
        {
            // Match: new ClassName(...) or new Some.Namespace.ClassName(...)
            var targets = classDecl.DescendantNodes()
                .OfType<ObjectCreationExpressionSyntax>()
                .Where(n => n.Type.ToString().Split('.').Last() == implClassName)
                .ToList();

            if (targets.Count == 0) return classDecl;

            var fieldRef = SyntaxFactory.IdentifierName(fieldName);

            // Pass 1: collect all local variable names that were initialised with new Foo()
            //         so we can rename their downstream usages to the field name.
            var localVarNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var newExpr in targets)
            {
                if (newExpr.Parent is EqualsValueClauseSyntax evc
                    && evc.Parent is VariableDeclaratorSyntax decl)
                    localVarNames.Add(decl.Identifier.Text);
            }

            // Pass 2: replace every new Foo(...) expression with the field reference.
            //         This covers plain assignments, using(var x = new Foo()), using var x = new Foo(), etc.
            classDecl = classDecl.ReplaceNodes(targets,
                (original, _) => fieldRef.WithTriviaFrom(original));

            // Pass 3: rename remaining usages of each local variable to the field name.
            //         e.g. repositorio.DoSomething() → _produtoRepositorio.DoSomething()
            foreach (var localVar in localVarNames)
            {
                var ids = classDecl.DescendantNodes()
                    .OfType<IdentifierNameSyntax>()
                    .Where(id => id.Identifier.Text == localVar
                              && !(id.Parent is VariableDeclaratorSyntax))
                    .ToList();

                if (ids.Count > 0)
                    classDecl = classDecl.ReplaceNodes(ids, (original, _) =>
                        SyntaxFactory.IdentifierName(fieldName).WithTriviaFrom(original));
            }

            // Pass 4: remove redundant "var x = _field;" and "using var x = _field;" declarations.
            //         After pass 2+3 all usages are already the field — the declaration is dead weight.
            var redundantDecls = classDecl.DescendantNodes()
                .OfType<LocalDeclarationStatementSyntax>()
                .Where(local =>
                    local.Declaration.Variables.Count == 1 &&
                    local.Declaration.Variables[0].Initializer?.Value is IdentifierNameSyntax initId &&
                    initId.Identifier.Text == fieldName)
                .ToList();

            if (redundantDecls.Count > 0)
                classDecl = classDecl.RemoveNodes(redundantDecls, SyntaxRemoveOptions.KeepNoTrivia);

            // Pass 5: unwrap "using (var x = _field) { ... }" blocks.
            //         The service lifetime is managed by DI, so the using wrapper is wrong.
            //         We keep iterating because parent nodes shift after each replacement.
            while (true)
            {
                var usingStmt = classDecl.DescendantNodes()
                    .OfType<UsingStatementSyntax>()
                    .FirstOrDefault(u =>
                        u.Declaration != null &&
                        u.Declaration.Variables.Count == 1 &&
                        u.Declaration.Variables[0].Initializer?.Value is IdentifierNameSyntax uid &&
                        uid.Identifier.Text == fieldName);

                if (usingStmt == null) break;

                var parentBlock = usingStmt.Parent as BlockSyntax;
                if (parentBlock == null) break;

                // Collect statements from the using body
                var bodyBlock   = usingStmt.Statement as BlockSyntax;
                var innerStmts  = bodyBlock != null
                    ? (IEnumerable<StatementSyntax>)bodyBlock.Statements
                    : new[] { usingStmt.Statement };

                // Rebuild parent block: replace using stmt with its inner statements
                var newStmts = new List<StatementSyntax>();
                foreach (var s in parentBlock.Statements)
                {
                    if (s == usingStmt)
                        newStmts.AddRange(innerStmts);
                    else
                        newStmts.Add(s);
                }

                var newBlock = parentBlock.WithStatements(SyntaxFactory.List(newStmts));
                classDecl = classDecl.ReplaceNode(parentBlock, newBlock);
            }

            return classDecl;
        }

        // ── Update constructors ───────────────────────────────────────────────

        private static ClassDeclarationSyntax UpdateConstructors(
            ClassDeclarationSyntax classDecl,
            string interfaceName, string qualifiedName, string fieldName, string paramName)
        {
            var ctors = classDecl.Members
                .OfType<ConstructorDeclarationSyntax>()
                .Where(c => c.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)))
                .ToList();

            return classDecl.ReplaceNodes(ctors, (original, _) =>
            {
                // Skip if already has this interface (match by short name)
                if (original.ParameterList.Parameters
                    .Any(p => p.Type?.ToString().Split('.').Last() == interfaceName))
                    return original;

                // Add parameter with fully qualified type
                var newParam = SyntaxFactory
                    .Parameter(SyntaxFactory.Identifier(paramName))
                    .WithType(SyntaxFactory.ParseTypeName(qualifiedName)
                        .WithTrailingTrivia(SyntaxFactory.Space));

                var updatedCtor = original.AddParameterListParameters(newParam);

                if (original.Body != null)
                {
                    var assignment = SyntaxFactory.ExpressionStatement(
                        SyntaxFactory.AssignmentExpression(
                            SyntaxKind.SimpleAssignmentExpression,
                            SyntaxFactory.IdentifierName(fieldName),
                            SyntaxFactory.IdentifierName(paramName)))
                        .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed);

                    var newStatements = new List<StatementSyntax> { assignment };
                    newStatements.AddRange(original.Body.Statements);
                    updatedCtor = updatedCtor.WithBody(
                        original.Body.WithStatements(SyntaxFactory.List(newStatements)));
                }

                return updatedCtor;
            });
        }

        // ── Naming helpers ────────────────────────────────────────────────────

        private static string ToFieldName(string interfaceName)
        {
            var name = StripLeadingI(interfaceName);
            return "_" + char.ToLower(name[0]) + name.Substring(1);
        }

        private static string ToParamName(string interfaceName)
        {
            var name = StripLeadingI(interfaceName);
            return char.ToLower(name[0]) + name.Substring(1);
        }

        private static string StripLeadingI(string name)
            => name.Length > 1 && name[0] == 'I' && char.IsUpper(name[1])
                ? name.Substring(1)
                : name;
    }
}
