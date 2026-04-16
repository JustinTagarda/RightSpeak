namespace RightSpeak.Models;

public sealed class HotkeyConfiguration
{
    public HotkeyConfiguration(uint readSelectedVirtualKey, uint readTypedTextVirtualKey, uint stopVirtualKey)
    {
        ReadSelectedVirtualKey = readSelectedVirtualKey;
        ReadTypedTextVirtualKey = readTypedTextVirtualKey;
        StopVirtualKey = stopVirtualKey;
    }

    public uint ReadSelectedVirtualKey { get; }

    public uint ReadTypedTextVirtualKey { get; }

    public uint StopVirtualKey { get; }
}
