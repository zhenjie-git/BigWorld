using System;
using System.Runtime.CompilerServices;

namespace BigWorldClient.UI.Framework
{
    /// <summary>
    /// Lightweight base class for ViewModels. Provides SetProperty + PropertyChanged
    /// without depending on Unity or external libraries.
    /// Usage: inheriting classes call SetProperty(ref field, value) in their property setters.
    /// </summary>
    public abstract class BindableObject
    {
        /// <summary>
        /// Fired when a property changes. The string argument is the property name.
        /// </summary>
        public event Action<string> PropertyChanged;

        /// <summary>
        /// Sets the backing field and fires PropertyChanged if the value actually changed.
        /// </summary>
        protected void SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(propertyName);
        }

        /// <summary>
        /// Manually fire PropertyChanged for a property. Use when a computed property
        /// depends on other properties changing (e.g. CanSubmit depends on Username and Password).
        /// </summary>
        protected void NotifyPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(propertyName);
        }
    }
}
