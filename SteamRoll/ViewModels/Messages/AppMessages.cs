using CommunityToolkit.Mvvm.Messaging.Messages;
using SteamRoll.Services;

namespace SteamRoll.ViewModels.Messages;

/// <summary>
/// Message sent when a peer is discovered on the network.
/// </summary>
public class PeerDiscoveredMessage : ValueChangedMessage<PeerInfo>
{
    public PeerDiscoveredMessage(PeerInfo peer) : base(peer) { }
}

/// <summary>
/// Message sent when a peer is lost from the network.
/// </summary>
public class PeerLostMessage : ValueChangedMessage<string>
{
    public PeerLostMessage(string peerId) : base(peerId) { }
}

/// <summary>
/// Message sent when the peer count changes.
/// </summary>
public class PeerCountChangedMessage : ValueChangedMessage<int>
{
    public PeerCountChangedMessage(int count) : base(count) { }
}

/// <summary>
/// Message sent when status text should be updated.
/// </summary>
public class StatusTextChangedMessage : ValueChangedMessage<string>
{
    public StatusTextChangedMessage(string status) : base(status) { }
}

/// <summary>
/// Message sent when loading state changes.
/// </summary>
public class LoadingStateChangedMessage
{
    public bool IsLoading { get; }
    public string? Message { get; }
    
    public LoadingStateChangedMessage(bool isLoading, string? message = null)
    {
        IsLoading = isLoading;
        Message = message;
    }
}

/// <summary>
/// Message requesting a library refresh.
/// </summary>
public class RefreshLibraryRequestMessage { }

/// <summary>
/// Message sent when library refresh is complete.
/// </summary>
public class LibraryRefreshedMessage { }

/// <summary>
/// Message sent when a game is packaged.
/// </summary>
public class GamePackagedMessage : ValueChangedMessage<string>
{
    public int AppId { get; }
    
    public GamePackagedMessage(string gameName, int appId) : base(gameName)
    {
        AppId = appId;
    }
}

/// <summary>
/// Message sent when a transfer starts.
/// </summary>
public class TransferStartedMessage
{
    public string GameName { get; }
    public bool IsSending { get; }
    
    public TransferStartedMessage(string gameName, bool isSending)
    {
        GameName = gameName;
        IsSending = isSending;
    }
}

/// <summary>
/// Message sent when a transfer completes.
/// </summary>
public class TransferCompletedMessage
{
    public string GameName { get; }
    public bool Success { get; }
    
    public TransferCompletedMessage(string gameName, bool success)
    {
        GameName = gameName;
        Success = success;
    }
}
