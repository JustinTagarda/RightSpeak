using System;

namespace RightSpeak.Services;

public interface IPrefetchedSpeechClip : IDisposable
{
    string Engine { get; }
    int TextLength { get; }
}
