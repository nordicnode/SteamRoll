using SteamRoll.Services;
using SteamRoll.Models;
using Xunit;

namespace SteamRoll.Tests;

public class SettingsServiceTests
{
    [Fact]
    public void Load_CreatesDefaultSettings_WhenFileDoesNotExist()
    {
        // This test verifies that default settings are created
        var service = new SettingsService();
        service.Load();
        
        Assert.NotNull(service.Settings);
        Assert.NotEmpty(service.Settings.OutputPath);
    }
    
    [Fact]
    public void Settings_HasValidDefaultPorts()
    {
        var settings = new AppSettings();
        
        Assert.True(settings.LanDiscoveryPort > 0 && settings.LanDiscoveryPort <= 65535);
        Assert.True(settings.TransferPort > 0 && settings.TransferPort <= 65535);
    }
    
    [Fact]
    public void Settings_HasValidDefaultWindowDimensions()
    {
        var settings = new AppSettings();
        
        Assert.True(settings.WindowWidth >= 400);
        Assert.True(settings.WindowHeight >= 300);
    }
    
    [Fact]
    public void Settings_TransferSpeedLimit_DefaultsToUnlimited()
    {
        var settings = new AppSettings();
        
        Assert.Equal(0, settings.TransferSpeedLimit);
    }
    
    [Fact]
    public void Settings_DefaultPackageMode_IsGoldberg()
    {
        var settings = new AppSettings();
        
        Assert.Equal(PackageMode.Goldberg, settings.DefaultPackageMode);
    }
}
