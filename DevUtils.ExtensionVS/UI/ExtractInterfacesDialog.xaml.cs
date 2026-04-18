using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using DevUtils.ExtensionVS.Models;
using Microsoft.VisualStudio.PlatformUI;

namespace DevUtils.ExtensionVS.UI
{
    public partial class ExtractInterfacesDialog : DialogWindow, INotifyPropertyChanged
    {
        private string _namespaceText;
        private string _outputFolder;
        private bool _useSourceLocation = true;

        public ExtractInterfacesDialog(
            IEnumerable<ClassInfo> allClasses,
            HashSet<string> preSelectedFilePaths,
            string defaultNamespace,
            string defaultOutputFolder)
        {
            _namespaceText = defaultNamespace ?? string.Empty;
            _outputFolder  = defaultOutputFolder ?? string.Empty;

            RootNodes = BuildTree(allClasses, preSelectedFilePaths);

            InitializeComponent();
            DataContext = this;
        }

        public ObservableCollection<ClassTreeNode> RootNodes { get; }

        public string NamespaceText
        {
            get => _namespaceText;
            set { _namespaceText = value; OnPropertyChanged(); }
        }

        public string OutputFolder
        {
            get => _outputFolder;
            set { _outputFolder = value; OnPropertyChanged(); }
        }

        public bool UseSourceLocation
        {
            get => _useSourceLocation;
            set
            {
                _useSourceLocation = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsOutputFolderEnabled));
                OnPropertyChanged(nameof(OutputFieldOpacity));
            }
        }

        public bool IsOutputFolderEnabled => !_useSourceLocation;
        public double OutputFieldOpacity   => _useSourceLocation ? 0.45 : 1.0;

        public ExtractionOptions Result { get; private set; }

        // ── Tree building ────────────────────────────────────────────────────────

        private static ObservableCollection<ClassTreeNode> BuildTree(
            IEnumerable<ClassInfo> classes,
            HashSet<string> preSelectedPaths)
        {
            var roots     = new ObservableCollection<ClassTreeNode>();
            var folderMap = new Dictionary<string, ClassTreeNode>(StringComparer.OrdinalIgnoreCase);

            foreach (var cls in classes.OrderBy(c => c.RelativeFolderPath ?? "").ThenBy(c => c.ClassName))
            {
                var folderNode = GetOrCreateFolder(cls.RelativeFolderPath ?? string.Empty, roots, folderMap);

                var classNode = new ClassTreeNode
                {
                    Name      = cls.ClassName,
                    IsFolder  = false,
                    ClassInfo = cls,
                    Parent    = folderNode,
                };
                classNode.SetCheckedSilent(preSelectedPaths.Contains(cls.FilePath));
                folderNode.Children.Add(classNode);
            }

            foreach (var root in roots)
                root.RecalculateCheckedState();

            return roots;
        }

        private static ClassTreeNode GetOrCreateFolder(
            string relativePath,
            ObservableCollection<ClassTreeNode> roots,
            Dictionary<string, ClassTreeNode> folderMap)
        {
            if (string.IsNullOrEmpty(relativePath))
            {
                const string key = "";
                if (!folderMap.TryGetValue(key, out var root))
                {
                    root = new ClassTreeNode { Name = "(project root)", IsFolder = true };
                    folderMap[key] = root;
                    roots.Add(root);
                }
                return root;
            }

            if (folderMap.TryGetValue(relativePath, out var existing))
                return existing;

            var norm       = relativePath.Replace('/', '\\');
            var parentPath = Path.GetDirectoryName(norm) ?? string.Empty;
            var folderName = Path.GetFileName(norm);

            var parent = GetOrCreateFolder(parentPath, roots, folderMap);
            var node   = new ClassTreeNode
            {
                Name     = folderName + "/",
                IsFolder = true,
                Parent   = parent,
            };
            folderMap[relativePath] = node;
            parent.Children.Add(node);
            return node;
        }

        // ── Event handlers ───────────────────────────────────────────────────────

        private void OnSelectAll(object sender, RoutedEventArgs e)
        {
            foreach (var node in RootNodes) node.IsChecked = true;
        }

        private void OnDeselectAll(object sender, RoutedEventArgs e)
        {
            foreach (var node in RootNodes) node.IsChecked = false;
        }

        private void OnNodeCheckBoxClick(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && cb.DataContext is ClassTreeNode node
                && node.IsFolder && node.IsChecked == null)
            {
                node.IsChecked = true; // folder skips indeterminate on user click
            }
        }

        private void OnBrowseOutputFolder(object sender, RoutedEventArgs e)
        {
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
            {
                dlg.Description    = "Select output folder for interface files";
                dlg.SelectedPath   = Directory.Exists(OutputFolder) ? OutputFolder : string.Empty;
                dlg.ShowNewFolderButton = true;
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    OutputFolder = dlg.SelectedPath;
            }
        }

        private void OnExtract(object sender, RoutedEventArgs e)
        {
            var selected = RootNodes.SelectMany(n => n.GetSelectedClasses()).ToList();

            if (selected.Count == 0)
            {
                MessageBox.Show(
                    "Please select at least one class to extract.",
                    "Extract Interfaces",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            Result = new ExtractionOptions
            {
                SelectedClasses    = selected,
                NamespaceOverride  = string.IsNullOrWhiteSpace(NamespaceText) ? null : NamespaceText.Trim(),
                OutputFolderOverride = UseSourceLocation || string.IsNullOrWhiteSpace(OutputFolder)
                                        ? null
                                        : OutputFolder.Trim(),
            };

            DialogResult = true;
        }

        private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

        // ── INotifyPropertyChanged ───────────────────────────────────────────────

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
