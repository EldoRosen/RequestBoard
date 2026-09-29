using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace RequestBoard
{
    /// <summary>Settings edited in the Torch UI and saved to RequestBoard.cfg.</summary>
    public class RequestBoardConfig : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private void SetValue<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        private bool _enabled = true;
        private string _webhookUrl = "";
        private string _serverName = "My Server";
        private string _currency = "SC";
        private int _depositPercent = 100;
        private long _minPrice = 1000;
        private long _maxPrice = 100000000;
        private double _maxHours = 72;
        private int _maxOpenPerPlayer = 3;
        private double _openExpiryHours = 24;
        private bool _burnDepositOnFail = false;
        private double _cooldownMinutes = 0;
        private bool _includeGps = true;
        private bool _nexusEnabled = false;
        private long _nexusChannelId = 887010042;

        public bool Enabled { get => _enabled; set => SetValue(ref _enabled, value); }
        public string WebhookUrl { get => _webhookUrl; set => SetValue(ref _webhookUrl, value); }
        public string ServerName { get => _serverName; set => SetValue(ref _serverName, value); }
        public string Currency { get => _currency; set => SetValue(ref _currency, value); }
        /// <summary>Deposit the accepting player must put up, as % of the price.</summary>
        public int DepositPercent { get => _depositPercent; set => SetValue(ref _depositPercent, value); }
        public long MinPrice { get => _minPrice; set => SetValue(ref _minPrice, value); }
        /// <summary>0 = unlimited.</summary>
        public long MaxPrice { get => _maxPrice; set => SetValue(ref _maxPrice, value); }
        /// <summary>Longest delivery time a requester may set.</summary>
        public double MaxHours { get => _maxHours; set => SetValue(ref _maxHours, value); }
        public int MaxOpenPerPlayer { get => _maxOpenPerPlayer; set => SetValue(ref _maxOpenPerPlayer, value); }
        /// <summary>How long a request waits for someone to accept before it expires (refunded).</summary>
        public double OpenExpiryHours { get => _openExpiryHours; set => SetValue(ref _openExpiryHours, value); }
        /// <summary>Minutes a player must wait after posting a request before posting another. 0 = no cooldown.</summary>
        public double CooldownMinutes { get => _cooldownMinutes; set => SetValue(ref _cooldownMinutes, value); }
        /// <summary>Attach the requester's in-game position (as a GPS string) to requests.</summary>
        public bool IncludeGps { get => _includeGps; set => SetValue(ref _includeGps, value); }
        /// <summary>Turns on cross-sector (cross-server) requests via the Nexus V3 ModAPI.</summary>
        public bool NexusEnabled { get => _nexusEnabled; set => SetValue(ref _nexusEnabled, value); }
        /// <summary>Custom Nexus mod channel ID for this plugin. Change only if it collides with another mod.</summary>
        public long NexusChannelId { get => _nexusChannelId; set => SetValue(ref _nexusChannelId, value); }
        /// <summary>false = failed deposit goes to the requester, true = deposit is destroyed.</summary>
        public bool BurnDepositOnFail { get => _burnDepositOnFail; set => SetValue(ref _burnDepositOnFail, value); }
    }
}
