namespace RightSpeak.Models;

public sealed class TextRetrievalResult
{
    private TextRetrievalResult(bool success, string? text, TextRetrievalSource? source, string message)
    {
        Success = success;
        Text = text;
        Source = source;
        Message = message;
    }

    public bool Success { get; }

    public string? Text { get; }

    public TextRetrievalSource? Source { get; }

    public string Message { get; }

    public static TextRetrievalResult Retrieved(string text, TextRetrievalSource source, string message)
    {
        return new TextRetrievalResult(true, text, source, message);
    }

    public static TextRetrievalResult Failed(string message, TextRetrievalSource? source = null)
    {
        return new TextRetrievalResult(false, null, source, message);
    }
}
