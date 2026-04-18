using System.Collections.Generic;

namespace DevUtils.ExtensionVS.Models
{
    public class DiOptions
    {
        public IReadOnlyList<DiRegistration> Registrations { get; set; }
        public string ClassName { get; set; }    // e.g. DependencyInjection
        public string MethodName { get; set; }   // e.g. AddMyServices
        public string Namespace { get; set; }
        public string OutputFolder { get; set; }
        public string Scope { get; set; }        // Scoped | Transient | Singleton
    }
}
