namespace RightSpeak.Models;

public sealed class SpeechVoice
{
    public SpeechVoice(string name, string displayName, string engine)
    {
        Name = name;
        DisplayName = displayName;
        Engine = engine;
    }

    public string Name { get; }

    public string DisplayName { get; }

    public string Engine { get; }
}
