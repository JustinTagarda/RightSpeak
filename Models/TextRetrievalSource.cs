namespace RightSpeak.Models;

public enum TextRetrievalSource
{
    UiAutomationSelection,
    UiAutomationParagraph,
    FocusedControl,
    FocusedControlDocument,
    WebpageMainContext,
    ClipboardFallback
}
