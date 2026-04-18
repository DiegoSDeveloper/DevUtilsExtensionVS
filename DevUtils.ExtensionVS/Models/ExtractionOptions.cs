using System.Collections.Generic;

namespace DevUtils.ExtensionVS.Models
{
    public class ExtractionOptions
    {
        public IReadOnlyList<ClassInfo> SelectedClasses { get; set; }
        public string NamespaceOverride { get; set; }    // null = use each class's own namespace
        public string OutputFolderOverride { get; set; } // null = place next to source file
    }
}
