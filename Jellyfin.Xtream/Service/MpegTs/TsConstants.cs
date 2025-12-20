namespace Jellyfin.Xtream.Service.MpegTs;

/// <summary>
/// Constants for MPEG-TS parsing.
/// </summary>
public static class TsConstants
{
    /// <summary>
    /// The standard size of an MPEG-TS packet.
    /// </summary>
    public const int PacketSize = 188;

    /// <summary>
    /// The sync byte value (0x47) found at the start of every MPEG-TS packet.
    /// </summary>
    public const byte SyncByte = 0x47;
}
