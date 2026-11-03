using System;
using SteamKit2;
using SteamKit2.Internal;

namespace QuickMatchAndroid.Lobby;

/// <summary>
/// Lobby chat, which SteamKit2's matchmaking handler does not cover, and members joining and leaving.
/// SteamKit2 reports the latter as callbacks, but the launcher's connection keeps its callback manager
/// to itself, so they are read here from the same messages. SteamKit2 hands every message to its
/// handlers in the order they were added, so its matchmaking handler (built in) has already updated its
/// lobby cache when this one (added later) raises an event.
/// </summary>
internal sealed class LobbyMessageHandler : ClientMsgHandler
{
    private readonly uint _appId;

    public LobbyMessageHandler(uint appId)
    {
        _appId = appId;
    }

    /// <summary>Lobby, sender, bytes. Raised on SteamKit2's receiving thread.</summary>
    public event Action<ulong, ulong, byte[]>? ChatReceived;

    /// <summary>Lobby, member. Raised on SteamKit2's receiving thread.</summary>
    public event Action<ulong, ulong>? MemberJoined;

    /// <summary>Lobby, member (ourselves too, when Steam takes us out). Raised on SteamKit2's receiving thread.</summary>
    public event Action<ulong, ulong>? MemberLeft;

    public void Send(ulong lobby, ulong target, byte[] data)
    {
        Client.Send(BuildChat(_appId, lobby, target, data));
    }

    /// <summary>Target 0 sends to every member of the lobby.</summary>
    internal static ClientMsgProtobuf<CMsgClientMMSSendLobbyChatMsg> BuildChat(uint appId, ulong lobby, ulong target, byte[] data)
    {
        var message = new ClientMsgProtobuf<CMsgClientMMSSendLobbyChatMsg>(EMsg.ClientMMSSendLobbyChatMsg);
        message.ProtoHeader.routing_appid = appId;
        message.Body.app_id = appId;
        message.Body.steam_id_lobby = lobby;
        message.Body.steam_id_target = target;
        message.Body.lobby_message = data;
        return message;
    }

    public override void HandleMsg(IPacketMsg packetMsg)
    {
        switch (packetMsg.MsgType)
        {
            case EMsg.ClientMMSLobbyChatMsg:
            {
                CMsgClientMMSLobbyChatMsg body = new ClientMsgProtobuf<CMsgClientMMSLobbyChatMsg>(packetMsg).Body;
                if (body.app_id == _appId && body.lobby_message != null)
                {
                    ChatReceived?.Invoke(body.steam_id_lobby, body.steam_id_sender, body.lobby_message);
                }
                break;
            }
            case EMsg.ClientMMSUserJoinedLobby:
            {
                CMsgClientMMSUserJoinedLobby body = new ClientMsgProtobuf<CMsgClientMMSUserJoinedLobby>(packetMsg).Body;
                if (body.app_id == _appId)
                {
                    MemberJoined?.Invoke(body.steam_id_lobby, body.steam_id_user);
                }
                break;
            }
            case EMsg.ClientMMSUserLeftLobby:
            {
                CMsgClientMMSUserLeftLobby body = new ClientMsgProtobuf<CMsgClientMMSUserLeftLobby>(packetMsg).Body;
                if (body.app_id == _appId)
                {
                    MemberLeft?.Invoke(body.steam_id_lobby, body.steam_id_user);
                }
                break;
            }
        }
    }
}
