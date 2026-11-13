using System;
using System.Collections.Generic;
using Brutal.Localization;
using KSA;
using GameStrings = Brutal.Localization.LStrings;

namespace KSAAutopilot
{
    /// <summary>Uses the game's shared localization provider, never the Windows display language.</summary>
    internal static class GameLocalization
    {
        private static readonly Dictionary<string, LString> Strings = new Dictionary<string, LString>();
        private static readonly LString LanguageName = new LString("Language", "English (United States)");
        private static bool _initialized;
        private static string _lastLocale;

        public static void Refresh()
        {
            try
            {
                if (!_initialized)
                {
                    foreach (var entry in L10n.Catalog)
                        Strings[entry.Key] = GameStrings.GetOrCreate("KSAAutopilot." + entry.Key, entry.Value.English);
                    L10n.ExternalTranslation = Translate;
                    _initialized = true;
                }
                string locale = "en-US";
                var provider = GameStrings.LocalizedStringProvider;
                if (provider?.Target is Language language) locale = language.Id;
                else if (provider != null && provider(LanguageName, out string displayName))
                {
                    foreach (Language registered in Languages.All.AsSpan())
                        if (string.Equals(displayName, registered.DisplayName, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(displayName, registered.Id, StringComparison.OrdinalIgnoreCase))
                        { locale = registered.Id; break; }
                }
                else if (Languages.All.Count == 1) locale = Languages.All.GetIndex(0).Id;
                // This KSA build ships en-US only and exposes no separate selected-locale setting.
                // Additional game language packs use the provider above for both selection and text.
                L10n.SetLocale(locale);
                if (_lastLocale != L10n.Locale)
                {
                    _lastLocale = L10n.Locale;
                    Diag.Info("LOCALIZATION", $"event=language_selected locale={L10n.Locale} registered_game_languages={Languages.All.Count} native_provider={provider != null} source=game");
                }
            }
            catch (Exception ex)
            {
                Diag.EveryError("game_language", 5, "LOCALIZATION", ex, "Reading the game's localization provider failed; keeping the last usable translation");
            }
        }

        private static string Translate(string key)
        {
            if (!Strings.TryGetValue(key, out LString text)) return null;
            string localized = text.Localized;
            return localized != text.Template ? localized : null;
        }
    }
}
