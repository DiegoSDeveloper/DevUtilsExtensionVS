using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using DevUtils.ExtensionVS.Models;
using DevUtils.ExtensionVS.Services;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.PlatformUI;
using Task = System.Threading.Tasks.Task;

namespace DevUtils.ExtensionVS.UI
{
    // ── Converters ────────────────────────────────────────────────────────────

    public class InjectionStatusIconConverter : IValueConverter
    {
        public static readonly InjectionStatusIconConverter Instance = new InjectionStatusIconConverter();

        public object Convert(object value, Type t, object p, CultureInfo c)
        {
            if (value is InjectionStatus s)
            {
                if (s == InjectionStatus.Safe)        return "✔  Safe";
                if (s == InjectionStatus.Warning)     return "⚠  Warning";
                if (s == InjectionStatus.Error)       return "✖  Error";
                if (s == InjectionStatus.AlreadyDone) return "✔  Done";
            }
            return "?";
        }

        public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotImplementedException();
    }

    public class InjectionStatusColorConverter : IValueConverter
    {
        public static readonly InjectionStatusColorConverter Instance = new InjectionStatusColorConverter();

        public object Convert(object value, Type t, object p, CultureInfo c)
        {
            if (value is InjectionStatus s)
            {
                if (s == InjectionStatus.Safe)        return Brushes.Green;
                if (s == InjectionStatus.Warning)     return Brushes.DarkOrange;
                if (s == InjectionStatus.Error)       return Brushes.Red;
                if (s == InjectionStatus.AlreadyDone) return Brushes.Gray;
            }
            return Brushes.Black;
        }

        public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotImplementedException();
    }

    public class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object v, Type t, object p, CultureInfo c)
            => v is bool b && b ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotImplementedException();
    }

    // ── Dialog ────────────────────────────────────────────────────────────────

    public partial class DiAnalysisDialog : DialogWindow, INotifyPropertyChanged
    {
        private readonly List<InjectionAnalysis> _allAnalyses;
        private readonly DiInjectorService _injector = new DiInjectorService();

        private DiRegistration _selectedInterface;
        private bool _showAlreadyDone = false;

        public DiAnalysisDialog(
            List<DiRegistration> registrations,
            List<InjectionAnalysis> analyses)
        {
            _allAnalyses     = analyses;
            InterfaceOptions = registrations;
            FilteredAnalyses = new ObservableCollection<InjectionAnalysis>();

            // Subscribe to each item so checkbox changes update HasSelection
            foreach (var a in analyses)
                a.PropertyChanged += OnAnalysisPropertyChanged;

            InitializeComponent();
            DataContext = this;

            // Set after DataContext so bindings are live before we populate
            SelectedInterface = registrations.FirstOrDefault();
        }

        private void OnAnalysisPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(InjectionAnalysis.IsSelected))
            {
                OnPropertyChanged(nameof(SelectedCount));
                OnPropertyChanged(nameof(HasSelection));
            }
        }

        public List<DiRegistration> InterfaceOptions { get; }

        public ObservableCollection<InjectionAnalysis> FilteredAnalyses { get; }

        public DiRegistration SelectedInterface
        {
            get => _selectedInterface;
            set
            {
                _selectedInterface = value;
                OnPropertyChanged();
                RefreshFilter();
            }
        }

        public bool ShowAlreadyDone
        {
            get => _showAlreadyDone;
            set
            {
                _showAlreadyDone = value;
                OnPropertyChanged();
                RefreshFilter();
            }
        }

        public int SelectedCount => _allAnalyses.Count(a => a.IsSelected);
        public bool HasSelection  => SelectedCount > 0;

        public int CountSafe    { get; private set; }
        public int CountWarning { get; private set; }
        public int CountError   { get; private set; }
        public int CountDone    { get; private set; }

        private void RefreshFilter()
        {
            FilteredAnalyses.Clear();

            foreach (var a in _allAnalyses)
            {
                if (_selectedInterface != null
                    && a.InterfaceName != _selectedInterface.InterfaceName)
                    continue;

                if (!_showAlreadyDone && a.Status == InjectionStatus.AlreadyDone)
                    continue;

                FilteredAnalyses.Add(a);
            }

            RefreshCounters();
        }

        private void RefreshCounters()
        {
            var scope = _selectedInterface == null
                ? _allAnalyses
                : _allAnalyses.Where(a => a.InterfaceName == _selectedInterface.InterfaceName).ToList();

            CountSafe    = scope.Count(a => a.Status == InjectionStatus.Safe);
            CountWarning = scope.Count(a => a.Status == InjectionStatus.Warning);
            CountError   = scope.Count(a => a.Status == InjectionStatus.Error);
            CountDone    = scope.Count(a => a.Status == InjectionStatus.AlreadyDone);

            OnPropertyChanged(nameof(CountSafe));
            OnPropertyChanged(nameof(CountWarning));
            OnPropertyChanged(nameof(CountError));
            OnPropertyChanged(nameof(CountDone));
            OnPropertyChanged(nameof(SelectedCount));
            OnPropertyChanged(nameof(HasSelection));
        }

        // ── Button handlers ───────────────────────────────────────────────────

        private void OnSelectAllSafe(object sender, RoutedEventArgs e)
        {
            foreach (var a in _allAnalyses.Where(a => a.Status == InjectionStatus.Safe
                                                    || a.Status == InjectionStatus.Warning))
                a.IsSelected = true;

            RefreshCounters();
        }

        private void OnDeselectAll(object sender, RoutedEventArgs e)
        {
            foreach (var a in _allAnalyses) a.IsSelected = false;
            RefreshCounters();
        }

        private void OnClose(object sender, RoutedEventArgs e) => Close();

        private void OnInject(object sender, RoutedEventArgs e)
        {
            _ = InjectSelectedAsync();
        }

        private async Task InjectSelectedAsync()
        {
            var selected = _allAnalyses.Where(a => a.IsSelected).ToList();
            if (selected.Count == 0) return;

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            DevUtilsPane.Log($"[Dev Utils] DI Inject — started ({selected.Count} class(es))");
            DevUtilsPane.Activate();
            Action<string> log = logMessage => DevUtilsPane.Log(logMessage);

            IsEnabled = false;
            int ok = 0, failed = 0;

            foreach (var analysis in selected)
            {
                try
                {
                    var result = await _injector.InjectAsync(analysis, log);
                    if (result)
                    {
                        analysis.Status     = InjectionStatus.AlreadyDone;
                        analysis.Reason     = "Injected successfully";
                        analysis.IsSelected = false;
                        ok++;
                    }
                    else
                    {
                        failed++;
                    }
                }
                catch (Exception ex)
                {
                    analysis.Status = InjectionStatus.Error;
                    analysis.Reason = $"Injection failed: {ex.Message}";
                    DevUtilsPane.Log($"  [error]    {analysis.TargetClassName}: {ex.Message}");
                    failed++;
                }
            }

            DevUtilsPane.Log($"[Dev Utils] DI Inject — done. Injected: {ok}, Failed: {failed}");

            RefreshFilter();
            IsEnabled = true;

            var msg = failed == 0
                ? $"Injected successfully into {ok} class(es)."
                : $"Injected into {ok} class(es). {failed} failed — see status column.";

            MessageBox.Show(msg, "DI Injection", MessageBoxButton.OK,
                failed == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
