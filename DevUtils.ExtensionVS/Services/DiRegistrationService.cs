using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using EnvDTE;
using DevUtils.ExtensionVS.Models;
using Microsoft.VisualStudio.Shell;

namespace DevUtils.ExtensionVS.Services
{
    internal class DiRegistrationService
    {
        public string Generate(DiOptions options, Project project, Action<string> log = null)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            Directory.CreateDirectory(options.OutputFolder);
            var filePath = Path.Combine(options.OutputFolder, options.ClassName + ".cs");

            File.WriteAllText(filePath, BuildCode(options), Encoding.UTF8);
            project.ProjectItems.AddFromFile(filePath);

            log?.Invoke($"  [generated] {filePath}");
            foreach (var reg in options.Registrations)
                log?.Invoke($"  services.Add{options.Scope}<{reg.InterfaceName}, {reg.ClassName}>();");

            return filePath;
        }

        private static string BuildCode(DiOptions options)
        {
            var sb = new StringBuilder();

            // Collect usings: namespaces different from the DI class namespace
            var usings = new SortedSet<string> { "Microsoft.Extensions.DependencyInjection" };
            foreach (var reg in options.Registrations)
            {
                if (!string.IsNullOrWhiteSpace(reg.InterfaceNamespace)
                    && reg.InterfaceNamespace != options.Namespace)
                    usings.Add(reg.InterfaceNamespace);

                if (!string.IsNullOrWhiteSpace(reg.ClassNamespace)
                    && reg.ClassNamespace != options.Namespace)
                    usings.Add(reg.ClassNamespace);
            }

            foreach (var ns in usings)
                sb.AppendLine($"using {ns};");

            sb.AppendLine();

            var indent = string.Empty;
            if (!string.IsNullOrWhiteSpace(options.Namespace))
            {
                sb.AppendLine($"namespace {options.Namespace}");
                sb.AppendLine("{");
                indent = "    ";
            }

            sb.AppendLine($"{indent}public static class {options.ClassName}");
            sb.AppendLine($"{indent}{{");
            sb.AppendLine($"{indent}    public static IServiceCollection {options.MethodName}(this IServiceCollection services)");
            sb.AppendLine($"{indent}    {{");

            foreach (var reg in options.Registrations)
                sb.AppendLine($"{indent}        services.Add{options.Scope}<{reg.InterfaceName}, {reg.ClassName}>();");

            sb.AppendLine();
            sb.AppendLine($"{indent}        return services;");
            sb.AppendLine($"{indent}    }}");
            sb.AppendLine($"{indent}}}");

            if (!string.IsNullOrWhiteSpace(options.Namespace))
                sb.AppendLine("}");

            return sb.ToString();
        }
    }
}
