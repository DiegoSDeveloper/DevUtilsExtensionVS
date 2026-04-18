using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using DevUtils.ExtensionVS.Models;
using Microsoft.VisualStudio.PlatformUI;

namespace DevUtils.ExtensionVS.UI
{
    public partial class RegisterDiDialog : DialogWindow, INotifyPropertyChanged
    {
        private string _className;
        private string _methodName;
        private string _namespace;
        private string _outputFolder;
        private string _selectedScope = "Scoped";

        public RegisterDiDialog(
            IEnumerable<DiRegistration> registrations,
            string defaultClassName,
            string defaultMethodName,
            string defaultNamespace,
            string defaultOutputFolder)
        {
            _className    = defaultClassName;
            _methodName   = defaultMethodName;
            _namespace    = defaultNamespace;
            _outputFolder = defaultOutputFolder;

            AllRegistrations = registrations.ToList();

            InitializeComponent();
            DataContext = this;

            var view = CollectionViewSource.GetDefaultView(AllRegistrations);
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(DiRegistration.FolderPath)));
            RegistrationsList.ItemsSource = view;
        }

        public List<DiRegistration> AllRegistrations { get; }

        public IReadOnlyList<string> Scopes { get; } = new[] { "Scoped", "Transient", "Singleton" };

        public string SelectedScope
        {
            get => _selectedScope;
            set { _selectedScope = value; OnPropertyChanged(); }
        }

        public string ClassName
        {
            get => _className;
            set { _className = value; OnPropertyChanged(); }
        }

        public string MethodName
        {
            get => _methodName;
            set { _methodName = value; OnPropertyChanged(); }
        }

        public string Namespace
        {
            get => _namespace;
            set { _namespace = value; OnPropertyChanged(); }
        }

        public string OutputFolder
        {
            get => _outputFolder;
            set { _outputFolder = value; OnPropertyChanged(); }
        }

        public DiOptions Result { get; private set; }

        private void OnSelectAll(object sender, RoutedEventArgs e)
        {
            foreach (var r in AllRegistrations) r.IsSelected = true;
        }

        private void OnDeselectAll(object sender, RoutedEventArgs e)
        {
            foreach (var r in AllRegistrations) r.IsSelected = false;
        }

        private void OnBrowseOutputFolder(object sender, RoutedEventArgs e)
        {
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
            {
                dlg.Description      = "Select output folder for the DI registration file";
                dlg.SelectedPath     = Directory.Exists(OutputFolder) ? OutputFolder : string.Empty;
                dlg.ShowNewFolderButton = true;
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    OutputFolder = dlg.SelectedPath;
            }
        }

        private void OnGenerate(object sender, RoutedEventArgs e)
        {
            var selected = AllRegistrations.Where(r => r.IsSelected).ToList();

            if (selected.Count == 0)
            {
                MessageBox.Show("Please select at least one registration.",
                    "Generate DI Registration", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(ClassName))
            {
                MessageBox.Show("Class name is required.",
                    "Generate DI Registration", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(MethodName))
            {
                MessageBox.Show("Method name is required.",
                    "Generate DI Registration", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Result = new DiOptions
            {
                Registrations = selected,
                ClassName     = ClassName.Trim(),
                MethodName    = MethodName.Trim(),
                Namespace     = string.IsNullOrWhiteSpace(Namespace) ? null : Namespace.Trim(),
                OutputFolder  = OutputFolder.Trim(),
                Scope         = SelectedScope,
            };

            DialogResult = true;
        }

        private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
