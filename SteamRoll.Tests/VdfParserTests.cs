using SteamRoll.Parsers;
using Xunit;

namespace SteamRoll.Tests;

public class VdfParserTests
{
    [Fact]
    public void Parse_SimpleKeyValue_ReturnsDictionary()
    {
        var vdf = "\"key\" \"value\"";
        var result = VdfParser.Parse(vdf);

        Assert.Single(result);
        Assert.Equal("value", result["key"]);
    }

    [Fact]
    public void Parse_NestedObject_ReturnsNestedDictionary()
    {
        var vdf = """
        "AppState"
        {
            "appid" "12345"
            "UserConfig"
            {
                "language" "english"
            }
        }
        """;

        var result = VdfParser.Parse(vdf);

        Assert.Single(result);
        var appState = Assert.IsType<Dictionary<string, object>>(result["AppState"]);
        Assert.Equal("12345", appState["appid"]);

        var userConfig = Assert.IsType<Dictionary<string, object>>(appState["UserConfig"]);
        Assert.Equal("english", userConfig["language"]);
    }

    [Fact]
    public void Parse_UnquotedKeys_Works()
    {
        var vdf = """
        AppState
        {
            appid "12345"
            StateFlags 4
        }
        """;

        var result = VdfParser.Parse(vdf);

        Assert.True(result.ContainsKey("AppState"));
        var appState = result["AppState"] as Dictionary<string, object>;
        Assert.NotNull(appState);
        Assert.Equal("12345", appState["appid"]);
        Assert.Equal("4", appState["StateFlags"]);
    }

    [Fact]
    public void Parse_MixedQuotes_Works()
    {
        var vdf = """
        "AppState"
        {
            appid "12345"
            "name" "Test Game"
            buildid 888
        }
        """;

        var result = VdfParser.Parse(vdf);

        Assert.True(result.ContainsKey("AppState"));
        var appState = result["AppState"] as Dictionary<string, object>;
        Assert.NotNull(appState);

        Assert.Equal("12345", appState["appid"]);
        Assert.Equal("Test Game", appState["name"]);
        Assert.Equal("888", appState["buildid"]);
    }
}
