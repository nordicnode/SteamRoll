using System;
using SteamRoll.Services;

namespace SteamRoll.Models;

public class RemoteGame
{
    public string Name { get; set; } = "";
    public long SizeBytes { get; set; }

    public string SizeDisplay => FormatUtils.FormatBytes(SizeBytes);
}
