namespace Potok.Backend.Core.Models.Gateway;

public record BootstrapResponse(
    int ContractVersion = 1,
    string[] EnabledActionGroups = null!,
    string[] EnabledContextualSurfaces = null!,
    int PrefsSchemaVersion = 1,
    string ResolvedLocale = "ru",
    object SafeUiFlags = null!
);
