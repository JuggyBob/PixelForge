namespace PixelForge.Presentation.Localization;

/// <summary>
/// Implemented by pages that need to rebuild view-model derived labels after a
/// runtime language change. Static XAML labels refresh through Loc automatically.
/// </summary>
public interface ILocalizationAwarePage
{
    void RefreshLocalization();
}
