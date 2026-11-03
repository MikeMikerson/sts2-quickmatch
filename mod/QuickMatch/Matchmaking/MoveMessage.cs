using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;

namespace QuickMatch.Matchmaking;

/// <summary>
/// Host to clients in a partial quick-match lobby: "our group is moving into this lobby".
/// The game registers every INetMessage type in loaded mod assemblies by reflection; all
/// quick-match players run this mod, so they agree on its message id.
/// </summary>
public struct QuickMatchMoveMessage : INetMessage, IPacketSerializable
{
    public ulong targetLobbyId;

    public bool ShouldBroadcast => false;

    public NetTransferMode Mode => NetTransferMode.Reliable;

    public LogLevel LogLevel => LogLevel.Info;

    public bool ShouldBuffer => true;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteULong(targetLobbyId);
    }

    public void Deserialize(PacketReader reader)
    {
        targetLobbyId = reader.ReadULong();
    }
}
