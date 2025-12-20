using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace SteamRoll.Parsers;

/// <summary>
/// Parser for Valve Data Format (VDF) files used by Steam for configuration
/// and manifest files (.vdf, .acf).
/// </summary>
public static class VdfParser
{
    /// <summary>
    /// Maximum recursion depth to prevent stack overflow on malformed files.
    /// </summary>
    private const int MAX_RECURSION_DEPTH = 50;
    
    /// <summary>
    /// Maximum number of tokens to parse to prevent excessive memory usage.
    /// </summary>
    private const int MAX_TOKEN_COUNT = 100000;

    /// <summary>
    /// Parses a VDF/ACF file and returns a nested dictionary structure.
    /// </summary>
    /// <param name="filePath">Path to the VDF file.</param>
    /// <returns>Dictionary representing the VDF structure.</returns>
    public static Dictionary<string, object> ParseFile(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"VDF file not found: {filePath}");

        var content = File.ReadAllText(filePath, Encoding.UTF8);
        return Parse(content);
    }

    /// <summary>
    /// Parses VDF content string and returns a nested dictionary structure.
    /// </summary>
    /// <param name="content">VDF content string.</param>
    /// <returns>Dictionary representing the VDF structure.</returns>
    public static Dictionary<string, object> Parse(string content)
    {
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var tokens = Tokenize(content);
        
        if (tokens.Count > MAX_TOKEN_COUNT)
            throw new InvalidDataException($"VDF file too large: {tokens.Count} tokens exceeds limit of {MAX_TOKEN_COUNT}");
        
        var index = 0;
        ParseObject(tokens, ref index, result, 0);
        return result;
    }


    /// <summary>
    /// Gets a nested value from the parsed VDF dictionary using dot notation.
    /// </summary>
    /// <param name="dict">The parsed VDF dictionary.</param>
    /// <param name="path">Dot-separated path (e.g., "AppState.appid").</param>
    /// <returns>The value at the path, or null if not found.</returns>
    public static string? GetValue(Dictionary<string, object> dict, string path)
    {
        var parts = path.Split('.');
        object? current = dict;

        foreach (var part in parts)
        {
            if (current is Dictionary<string, object> currentDict)
            {
                if (currentDict.TryGetValue(part, out var value))
                {
                    current = value;
                }
                else
                {
                    return null;
                }
            }
            else
            {
                return null;
            }
        }

        return current?.ToString();
    }

    /// <summary>
    /// Gets a nested dictionary section from the parsed VDF.
    /// </summary>
    public static Dictionary<string, object>? GetSection(Dictionary<string, object> dict, string path)
    {
        var parts = path.Split('.');
        object? current = dict;

        foreach (var part in parts)
        {
            if (current is Dictionary<string, object> currentDict)
            {
                if (currentDict.TryGetValue(part, out var value))
                {
                    current = value;
                }
                else
                {
                    return null;
                }
            }
            else
            {
                return null;
            }
        }

        return current as Dictionary<string, object>;
    }

    private static List<string> Tokenize(string content)
    {
        var tokens = new List<string>();
        // Matches:
        // 1. Quoted strings (capturing content)
        // 2. Braces { or }
        // 3. Unquoted strings (alphanumeric/symbols) - typical in Source engine files but not strict JSON
        var regex = new Regex(@"""([^""\\]*(?:\\.[^""\\]*)*)""|(\{|\})|([a-zA-Z0-9_\-\.]+)", RegexOptions.Compiled);

        foreach (Match match in regex.Matches(content))
        {
            if (match.Groups[1].Success)
            {
                // Quoted string - unescape backslashes
                var value = match.Groups[1].Value.Replace("\\\\", "\\");
                tokens.Add(value);
            }
            else if (match.Groups[2].Success)
            {
                // Brace
                tokens.Add(match.Groups[2].Value);
            }
            else if (match.Groups[3].Success)
            {
                // Unquoted string
                tokens.Add(match.Groups[3].Value);
            }
        }

        return tokens;
    }

    private static void ParseObject(List<string> tokens, ref int index, Dictionary<string, object> result, int depth)
    {
        if (depth > MAX_RECURSION_DEPTH)
            throw new InvalidDataException($"VDF structure too deep: exceeded maximum recursion depth of {MAX_RECURSION_DEPTH}");
        
        while (index < tokens.Count)
        {
            var token = tokens[index];

            if (token == "}")
            {
                index++;
                return;
            }

            if (token == "{")
            {
                index++;
                continue;
            }

            // This is a key
            var key = token;
            index++;

            if (index >= tokens.Count)
                break;

            var nextToken = tokens[index];

            if (nextToken == "{")
            {
                // Nested object
                index++;
                var nested = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                ParseObject(tokens, ref index, nested, depth + 1);
                result[key] = nested;
            }
            else if (nextToken != "}")
            {
                // Key-value pair
                result[key] = nextToken;
                index++;
            }
        }
    }
}

