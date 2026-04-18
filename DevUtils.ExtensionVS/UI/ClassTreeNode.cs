using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using DevUtils.ExtensionVS.Models;

namespace DevUtils.ExtensionVS.UI
{
    public class ClassTreeNode : INotifyPropertyChanged
    {
        private bool? _isChecked = false;

        public string Name { get; set; }
        public bool IsFolder { get; set; }
        public ClassInfo ClassInfo { get; set; }
        public ClassTreeNode Parent { get; set; }
        public ObservableCollection<ClassTreeNode> Children { get; } = new ObservableCollection<ClassTreeNode>();

        public bool? IsChecked
        {
            get => _isChecked;
            set => SetChecked(value, propagateDown: true, propagateUp: true);
        }

        internal void SetCheckedSilent(bool value) => _isChecked = value;

        private void SetChecked(bool? value, bool propagateDown, bool propagateUp)
        {
            if (_isChecked == value) return;
            _isChecked = value;
            OnPropertyChanged(nameof(IsChecked));

            if (propagateDown && value.HasValue)
                foreach (var child in Children)
                    child.SetChecked(value, propagateDown: true, propagateUp: false);

            if (propagateUp)
                Parent?.RecalculateCheckedState();
        }

        internal void RecalculateCheckedState()
        {
            foreach (var child in Children.Where(c => c.IsFolder))
                child.RecalculateCheckedState();

            if (!IsFolder || Children.Count == 0) return;

            var allTrue  = Children.All(c => c._isChecked == true);
            var allFalse = Children.All(c => c._isChecked == false);
            bool? newVal = allTrue ? (bool?)true : allFalse ? (bool?)false : (bool?)null;

            if (_isChecked == newVal) return;
            _isChecked = newVal;
            OnPropertyChanged(nameof(IsChecked));
        }

        public IEnumerable<ClassInfo> GetSelectedClasses()
        {
            if (!IsFolder && _isChecked == true && ClassInfo != null)
                yield return ClassInfo;
            foreach (var child in Children)
                foreach (var c in child.GetSelectedClasses())
                    yield return c;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
