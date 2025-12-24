using SteamRoll.Services;
using SteamRoll.Models;
using Xunit;

namespace SteamRoll.Tests;

public class DrmDetectorTests
{
    [Fact]
    public void DrmType_HasExpectedValues()
    {
        // Verify DRM types exist
        Assert.Equal(0, (int)DrmType.None);
        Assert.Equal(1, (int)DrmType.SteamStub);
        Assert.Equal(2, (int)DrmType.SteamCEG);
        Assert.Equal(3, (int)DrmType.Denuvo);
    }
    
    [Theory]
    [InlineData(DrmType.None, true)]
    [InlineData(DrmType.SteamStub, true)]
    [InlineData(DrmType.SteamCEG, true)]
    [InlineData(DrmType.Denuvo, false)]
    [InlineData(DrmType.VMProtect, false)]
    [InlineData(DrmType.Themida, false)]
    public void CalculateCompatibility_ReturnsExpectedDirection(DrmType drmType, bool isPositive)
    {
        var result = new DrmAnalysisResult();
        result.AddDrm(drmType, "Test detection");
        result.CalculateCompatibility();
        
        if (isPositive)
        {
            Assert.True(result.CompatibilityScore >= 0.5, 
                $"Expected {drmType} to have high compatibility, but got {result.CompatibilityScore}");
        }
        else
        {
            Assert.True(result.CompatibilityScore <= 0.5, 
                $"Expected {drmType} to have low compatibility, but got {result.CompatibilityScore}");
        }
    }
    
    [Fact]
    public void DrmAnalysisResult_AddDrm_TracksMultipleDrmTypes()
    {
        var result = new DrmAnalysisResult();
        
        result.AddDrm(DrmType.SteamStub, "Steam API detected");
        result.AddDrm(DrmType.Denuvo, "Denuvo detected");
        
        Assert.Contains(result.DetectedDrmList, d => d.Type == DrmType.SteamStub);
        Assert.Contains(result.DetectedDrmList, d => d.Type == DrmType.Denuvo);
        Assert.Equal(2, result.DetectedDrmList.Count);
    }
    
    [Fact]
    public void DrmAnalysisResult_CalculateCompatibility_DenuvoGivesLowScore()
    {
        var result = new DrmAnalysisResult();
        result.AddDrm(DrmType.Denuvo, "Denuvo detected");
        result.CalculateCompatibility();
        
        Assert.True(result.CompatibilityScore < 0.5);
        Assert.False(result.IsGoldbergCompatible);
    }
    
    [Fact]
    public void DrmAnalysisResult_CalculateCompatibility_SteamStubGivesHighScore()
    {
        var result = new DrmAnalysisResult();
        result.AddDrm(DrmType.SteamStub, "Steam API detected");
        result.CalculateCompatibility();
        
        Assert.True(result.CompatibilityScore >= 0.5);
        Assert.True(result.IsGoldbergCompatible);
    }
    
    [Fact]
    public void DrmAnalysisResult_EmptyResult_IsGoldbergCompatible()
    {
        var result = new DrmAnalysisResult();
        result.CalculateCompatibility();
        
        // No DRM detected should result in high compatibility
        Assert.True(result.CompatibilityScore >= 0.8);
        Assert.True(result.IsGoldbergCompatible);
    }
}
