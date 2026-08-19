using Baballonia.Contracts;

namespace Baballonia.Services;

public class ActivationService(
    IThemeSelectorService themeSelectorService,
    ILanguageSelectorService languageSelectorService)
    : IActivationService
{
    public void Activate(object activationArgs)
    {
        languageSelectorService.Initialize();
        languageSelectorService.SetRequestedLanguage();
        themeSelectorService.Initialize();
        themeSelectorService.SetRequestedTheme();

        // Desktop-only activation continues below when additional startup work is enabled.
        if (!Utils.IsSupportedDesktopOS) return;
    }
}
