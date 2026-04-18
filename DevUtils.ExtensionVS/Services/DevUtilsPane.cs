using System;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace DevUtils.ExtensionVS.Services
{
    internal static class DevUtilsPane
    {
        private static readonly Guid PaneGuid = new Guid("E2B4F2A1-3C5D-4E6F-7A8B-9C0D1E2F3A4B");

        public static IVsOutputWindowPane GetOrCreate()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var outputWindow = Package.GetGlobalService(typeof(SVsOutputWindow)) as IVsOutputWindow;
            if (outputWindow == null) return null;

            var guid = PaneGuid;
            outputWindow.GetPane(ref guid, out var pane);
            if (pane == null)
            {
                outputWindow.CreatePane(ref guid, "Dev Utils", 1, 1);
                outputWindow.GetPane(ref guid, out pane);
            }
            return pane;
        }

        public static void Log(string message)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            GetOrCreate()?.OutputString(message + "\r\n");
        }

        public static void Activate()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            GetOrCreate()?.Activate();
        }
    }
}
