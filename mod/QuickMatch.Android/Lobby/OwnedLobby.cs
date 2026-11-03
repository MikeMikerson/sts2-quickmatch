using System.Collections.Generic;
using SteamKit2;

namespace QuickMatchAndroid.Lobby;

/// <summary>
/// What we set on a lobby we own. Steam replaces type, size and every data value of a lobby in one
/// request, so each change sends the whole of it again.
/// </summary>
internal sealed class OwnedLobby
{
    private readonly object _lock = new();
    private readonly Dictionary<string, string> _data = new();
    private ELobbyType _type;
    private ELobbyType? _typeBeforeClosing;

    public OwnedLobby(ELobbyType type, int maxMembers)
    {
        _type = type;
        MaxMembers = maxMembers;
    }

    public int MaxMembers { get; }

    /// <summary>1 while a send of the current state is scheduled.</summary>
    public int SendScheduled;

    public void Set(string key, string value)
    {
        lock (_lock)
        {
            _data[key] = value;
        }
    }

    public bool Delete(string key)
    {
        lock (_lock)
        {
            return _data.Remove(key);
        }
    }

    public string Get(string key)
    {
        lock (_lock)
        {
            return _data.TryGetValue(key, out string? value) ? value : "";
        }
    }

    public void SetType(ELobbyType type)
    {
        lock (_lock)
        {
            if (_typeBeforeClosing.HasValue)
            {
                // Closed for joining: the new type applies when it opens again.
                _typeBeforeClosing = type;
            }
            else
            {
                _type = type;
            }
        }
    }

    /// <summary>
    /// Steam has no joinable switch on this connection, so a closed lobby is made private (entered by
    /// invitation only) and gets its type back when opened.
    /// </summary>
    public void SetJoinable(bool joinable)
    {
        lock (_lock)
        {
            if (!joinable && !_typeBeforeClosing.HasValue)
            {
                _typeBeforeClosing = _type;
                _type = ELobbyType.Private;
            }
            else if (joinable && _typeBeforeClosing is ELobbyType type)
            {
                _type = type;
                _typeBeforeClosing = null;
            }
        }
    }

    public (ELobbyType Type, Dictionary<string, string> Data) Snapshot()
    {
        lock (_lock)
        {
            return (_type, new Dictionary<string, string>(_data));
        }
    }
}
