using EnvDTE;

namespace DevUtils.ExtensionVS.Models
{
    public class ClassInfo
    {
        public string ClassName { get; set; }
        public string FilePath { get; set; }
        public string Namespace { get; set; }
        public string RelativeFolderPath { get; set; }
        public ProjectItem ProjectItem { get; set; }
    }
}
