using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BillPro.Helpers
{
    // This base class handles all the heavy lifting for UI updates
    public class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        // Triggers the UI update for a specific property
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        // A helper method that checks if the value actually changed before updating,
        // saving performance and condensing your property syntax!
        protected bool SetProperty<T>(ref T backingField, T value, [CallerMemberName] string propertyName = null)
        {
            // If the value is the same, do nothing
            if (Equals(backingField, value)) return false;

            backingField = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }
}