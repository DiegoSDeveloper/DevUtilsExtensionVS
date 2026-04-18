using DevUtils.ExtensionVS.Commands;
using Microsoft.VisualStudio.Shell;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using Task = System.Threading.Tasks.Task;

namespace DevUtils.ExtensionVS
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(DevUtilsPackage.PackageGuidString)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideAutoLoad(Microsoft.VisualStudio.VSConstants.UICONTEXT.SolutionExists_string,
        PackageAutoLoadFlags.BackgroundLoad)]
    public sealed class DevUtilsPackage : AsyncPackage
    {
        public const string PackageGuidString = "d19c5749-9935-4a2f-9c20-1ecab900fcdb";

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await base.InitializeAsync(cancellationToken, progress);
            await ExtractInterfacesCommand.InitializeAsync(this);
            await RegisterDiCommand.InitializeAsync(this);
            await AnalyzeDiCommand.InitializeAsync(this);
            await FormatAllFilesCommand.InitializeAsync(this);
        }
    }
}
