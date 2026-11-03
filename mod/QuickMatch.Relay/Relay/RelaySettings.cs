using System;
using System.Collections.Generic;

namespace QuickMatchRelay.Relay;

/// <summary>
/// Whether the fast link is on and which relay it uses, read from <see cref="FileName"/> next to the
/// mod. There is no built-in relay: without the file, or without a <c>relay</c> line, the fast link is off
/// and Quick Match uses Steam lobby chat. The file holds <c>key = value</c> lines and <c>#</c> comments:
///
///   enabled = false                                    turns the fast link off
///   relay = https://my-relay.my-account.workers.dev    the relay to use (README: run your own)
///
/// A relay address is a host name on HTTPS's port 443, nothing else: no other port, path, query, user
/// name or IP address. Any problem in the file turns the fast link off, so a mistyped address never
/// silently sends a match to a relay the player did not choose.
/// </summary>
internal sealed record RelaySettings(bool Enabled, Uri? Relay)
{
    public const string FileName = "QuickMatchRelay.cfg";

    /// <summary>Settings from the file's text (null: no file). Problems are reported and turn the fast link off, and so does a missing relay.</summary>
    public static RelaySettings Parse(string? text, Action<string> problem)
    {
        bool enabled = true;
        Uri? relay = null;
        bool broken = false;
        var seen = new HashSet<string>();
        string[] lines = (text ?? "").Split('\n');
        for (int number = 1; number <= lines.Length; number++)
        {
            string line = lines[number - 1].Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }
            int equals = line.IndexOf('=');
            if (equals <= 0)
            {
                problem($"{FileName} line {number}: expected key = value");
                broken = true;
                continue;
            }
            string key = line[..equals].Trim().ToLowerInvariant();
            string value = line[(equals + 1)..].Trim();
            if (!seen.Add(key))
            {
                problem($"{FileName} line {number}: {key} is set twice");
                broken = true;
                continue;
            }
            switch (key)
            {
                case "enabled":
                    if (value.ToLowerInvariant() is "true" or "on" or "yes")
                    {
                        enabled = true;
                    }
                    else if (value.ToLowerInvariant() is "false" or "off" or "no")
                    {
                        enabled = false;
                    }
                    else
                    {
                        problem($"{FileName} line {number}: enabled is true or false, not \"{value}\"");
                        broken = true;
                    }
                    break;
                case "relay":
                    if (TryParseAddress(value, out Uri? address, out string? why))
                    {
                        relay = address!;
                    }
                    else
                    {
                        problem($"{FileName} line {number}: relay address {why}");
                        broken = true;
                    }
                    break;
                default:
                    problem($"{FileName} line {number}: unknown setting \"{key}\" (known: enabled, relay)");
                    broken = true;
                    break;
            }
        }
        return new RelaySettings(enabled && !broken && relay != null, relay);
    }

    /// <summary>
    /// A relay address as <c>wss://host</c>, from <c>https://host</c> or <c>wss://host</c> (a trailing
    /// slash and an explicit :443 are fine). False with the reason for anything else.
    /// </summary>
    public static bool TryParseAddress(string value, out Uri? address, out string? why)
    {
        address = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
        {
            why = $"\"{value}\" is not an address";
            return false;
        }
        why = uri switch
        {
            _ when uri.Scheme is not ("https" or "wss") => "must start with https:// or wss://",
            _ when uri.Port != 443 => "must use port 443",
            _ when uri.UserInfo.Length > 0 => "may not hold a user name or password",
            _ when uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0 => "is a host name only, without a path or query",
            _ when uri.HostNameType != UriHostNameType.Dns || !uri.Host.Contains('.') => "must be a host name, not an IP address",
            _ => null,
        };
        if (why != null)
        {
            return false;
        }
        address = new Uri($"wss://{uri.IdnHost}");
        return true;
    }
}
