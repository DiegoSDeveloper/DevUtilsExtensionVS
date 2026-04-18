using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using EnvDTE;

namespace DevUtils.ExtensionVS.Models
{
    public enum InjectionStatus { AlreadyDone, Safe, Warning, Error }

    public class InjectionAnalysis : INotifyPropertyChanged
    {
        private bool _isSelected;
        private InjectionStatus _status;
        private string _reason;

        public string TargetClassName { get; set; }
        public string TargetFilePath { get; set; }
        public string FolderPath { get; set; }
        public ProjectItem TargetProjectItem { get; set; }
        public int ConstructorCount { get; set; }

        public string InterfaceName { get; set; }
        public string InterfaceNamespace { get; set; }
        public string ImplementationClassName { get; set; }

        public InjectionStatus Status
        {
            get => _status;
            set { _status = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanSelect)); }
        }

        public string Reason
        {
            get => _reason;
            set { _reason = value; OnPropertyChanged(); }
        }

        public string DisplayPath
        {
            get
            {
                var file = Path.GetFileName(TargetFilePath ?? string.Empty);
                return string.IsNullOrEmpty(FolderPath) || FolderPath == "(root)"
                    ? file
                    : FolderPath + "\\" + file;
            }
        }

        public bool CanSelect => Status == InjectionStatus.Safe || Status == InjectionStatus.Warning;

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (!CanSelect) return;
                _isSelected = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
