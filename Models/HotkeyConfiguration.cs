using RightSpeak.Interop;

namespace RightSpeak.Models;

public sealed class HotkeyConfiguration
{
    public HotkeyConfiguration(
        HotKeyModifiers modifiers,
        uint readSelectedVirtualKey,
        uint readParagraphVirtualKey,
        uint readDocumentVirtualKey,
        uint stopVirtualKey)
    {
        Modifiers = modifiers;
        ReadSelectedVirtualKey = readSelectedVirtualKey;
        ReadParagraphVirtualKey = readParagraphVirtualKey;
        ReadDocumentVirtualKey = readDocumentVirtualKey;
        StopVirtualKey = stopVirtualKey;
    }

    public HotKeyModifiers Modifiers { get; }
    public uint ReadSelectedVirtualKey { get; }
    public uint ReadParagraphVirtualKey { get; }
    public uint ReadDocumentVirtualKey { get; }
    public uint StopVirtualKey { get; }
}
