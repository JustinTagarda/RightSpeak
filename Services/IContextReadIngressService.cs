using System;

namespace RightSpeak.Services;

public interface IContextReadIngressService : IDisposable
{
    event EventHandler<string>? ReadRequested;

    void Start();

    void Stop();
}
