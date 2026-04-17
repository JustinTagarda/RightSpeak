namespace RightSpeak.Models;

public sealed class TextRetrievalResult
{
    private TextRetrievalResult(bool success, string? text, TextRetrievalSource? source, string message, bool shouldRetry)
    {
        Success = success;
        Text = text;
        Source = source;
        Message = message;
        ShouldRetry = shouldRetry;
    }

    public bool Success { get; }

    public string? Text { get; }

    public TextRetrievalSource? Source { get; }

    public string Message { get; }

    public bool ShouldRetry { get; }

    public static TextRetrievalResult Retrieved(string text, TextRetrievalSource source, string message, bool shouldRetry = false)
    {
        return new TextRetrievalResult(true, text, source, message, shouldRetry);
    }

    public static TextRetrievalResult Failed(string message, TextRetrievalSource? source = null, bool shouldRetry = false)
    {
        return new TextRetrievalResult(false, null, source, message, shouldRetry);
    }

    public TextRetrievalResult WithRetrySuggested(bool shouldRetry)
    {
        if (ShouldRetry == shouldRetry)
        {
            return this;
        }

        return new TextRetrievalResult(Success, Text, Source, Message, shouldRetry);
    }
}
