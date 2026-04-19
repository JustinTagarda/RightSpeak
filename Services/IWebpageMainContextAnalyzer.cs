namespace RightSpeak.Services;

public interface IWebpageMainContextAnalyzer
{
    WebpageAnalysisResult AnalyzeForegroundWindow();
    WebpageAnalysisResult AnalyzeWindow(nint windowHandle);
}
