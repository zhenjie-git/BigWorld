using System;
using System.Runtime.CompilerServices;

namespace BigWorldClient.UI.Framework
{

    public abstract class BindableObject
    {

        public event Action<string> PropertyChanged;

        protected void SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(propertyName);
        }

        protected void NotifyPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(propertyName);
        }
    }
}
