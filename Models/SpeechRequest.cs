namespace RightSpeak.Models;

public sealed class SpeechRequest
{
    public SpeechRequest(string text, SpeechOptions? options = null)
    {
        Text = text;
        Options = options ?? new SpeechOptions();
    }

    public string Text { get; }

    public SpeechOptions Options { get; }
}
