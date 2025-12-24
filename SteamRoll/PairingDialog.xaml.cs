using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SteamRoll.Services;
using SteamRoll.Services.Security;

namespace SteamRoll;

/// <summary>
/// Dialog for pairing devices for encrypted transfers.
/// </summary>
public partial class PairingDialog : Window
{
    private readonly PairingService _pairingService;
    private readonly SettingsService _settingsService;
    private string _currentCode = "";
    private bool _isShowingCode = true;

    public bool PairingSuccessful { get; private set; }
    public string? PairedDeviceId { get; private set; }

    public PairingDialog(SettingsService settingsService)
    {
        InitializeComponent();
        _settingsService = settingsService;
        _pairingService = new PairingService();
        
        // Generate initial code
        GenerateNewCode();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1)
        {
            DragMove();
        }
    }

    private void GenerateNewCode()
    {
        _currentCode = _pairingService.GeneratePairingCode();
        PairingCodeDisplay.Text = FormatCode(_currentCode);
    }

    private string FormatCode(string code)
    {
        // Add spacing between digits for readability
        if (code.Length == 6)
            return $"{code[0]} {code[1]} {code[2]} {code[3]} {code[4]} {code[5]}";
        return code;
    }

    private void ShowCodeTab_Click(object sender, RoutedEventArgs e)
    {
        _isShowingCode = true;
        ShowCodePanel.Visibility = Visibility.Visible;
        EnterCodePanel.Visibility = Visibility.Collapsed;
        
        ShowCodeTab.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#A371F7"));
        ShowCodeTab.Foreground = Brushes.White;
        ShowCodeTab.FontWeight = FontWeights.SemiBold;
        
        EnterCodeTab.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#21262D"));
        EnterCodeTab.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#C9D1D9"));
        EnterCodeTab.FontWeight = FontWeights.Normal;
        
        PairButton.IsEnabled = false;
    }

    private void EnterCodeTab_Click(object sender, RoutedEventArgs e)
    {
        _isShowingCode = false;
        ShowCodePanel.Visibility = Visibility.Collapsed;
        EnterCodePanel.Visibility = Visibility.Visible;
        
        EnterCodeTab.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#A371F7"));
        EnterCodeTab.Foreground = Brushes.White;
        EnterCodeTab.FontWeight = FontWeights.SemiBold;
        
        ShowCodeTab.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#21262D"));
        ShowCodeTab.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#C9D1D9"));
        ShowCodeTab.FontWeight = FontWeights.Normal;
        
        ValidateInput();
    }

    private void GenerateNewCode_Click(object sender, RoutedEventArgs e)
    {
        GenerateNewCode();
    }

    private void RemoteCodeBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Only allow digits
        var text = RemoteCodeBox.Text;
        var filtered = new string(text.Where(char.IsDigit).ToArray());
        if (filtered != text)
        {
            RemoteCodeBox.Text = filtered;
            RemoteCodeBox.CaretIndex = filtered.Length;
        }
        
        ValidateInput();
    }

    private void ValidateInput()
    {
        if (_isShowingCode)
        {
            PairButton.IsEnabled = false;
            return;
        }
        
        var hasValidCode = RemoteCodeBox.Text.Length == 6;
        var hasValidIp = !string.IsNullOrWhiteSpace(RemoteIpBox.Text);
        
        PairButton.IsEnabled = hasValidCode && hasValidIp;
        StatusMessage.Text = "";
    }

    private void Pair_Click(object sender, RoutedEventArgs e)
    {
        var remoteIp = RemoteIpBox.Text.Trim();
        var remoteCode = RemoteCodeBox.Text.Trim();
        var localDeviceId = _settingsService.Settings.DeviceId;
        
        // Derive shared key from code
        var sharedKey = _pairingService.DeriveKey(remoteCode, localDeviceId, remoteIp);
        
        // Save the paired device
        _pairingService.SavePairedDevice(remoteIp, $"Device at {remoteIp}", sharedKey);
        
        PairingSuccessful = true;
        PairedDeviceId = remoteIp;
        
        LogService.Instance.Info($"Paired with device at {remoteIp}", "PairingDialog");
        
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
