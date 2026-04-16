using RightSpeak.Models;

namespace RightSpeak.Services;

public interface IAppSettingsService
{
    AppSettings Current { get; }

    void Save();
}
