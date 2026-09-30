using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace RequestBoard
{
    public class RequestBoardConfig : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private void SetValue<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        private string _serviceUrl = "http://localhost:5080";
        private string _serverName = "My Server";
        private int _syncIntervalSeconds = 60;

        public string ServiceUrl { get => _serviceUrl; set => SetValue(ref _serviceUrl, value); }
        public string ServerName { get => _serverName; set => SetValue(ref _serverName, value); }
        public int SyncIntervalSeconds { get => _syncIntervalSeconds; set => SetValue(ref _syncIntervalSeconds, value); }
    }
}
