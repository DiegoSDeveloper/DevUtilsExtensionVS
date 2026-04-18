using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DevUtils.ExtensionVS.Models
{
    public class DiRegistration : INotifyPropertyChanged
    {
        private bool _isSelected = true;

        public string InterfaceName { get; set; }      // short name, e.g. IMyService
        public string ClassName { get; set; }           // short name, e.g. MyService
        public string InterfaceNamespace { get; set; }
        public string ClassNamespace { get; set; }
        public string FolderPath { get; set; }          // relative folder, for grouping

        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
        }

        public string DisplayName => $"{InterfaceName}  ←  {ClassName}";

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
