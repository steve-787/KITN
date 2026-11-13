using System.Globalization;
using System.Text.RegularExpressions;
using KSAAutopilot;

internal static class LocalizationTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Restore() { L10n.ExternalTranslation = null; L10n.SetLocale("en-US"); }

    public static void GameCulture()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            L10n.SetLocale("en-US");
            Check(L10n.Get("action.countdown") == "Start countdown", "English game UI followed the Windows language");
            Check(1234.5.ToString("N1", L10n.Culture) == "1,234.5", "Numbers ignored the game locale");
            L10n.SetLocale("de-DE");
            Check(L10n.Get("action.countdown") == "Countdown starten", "German translation missing");
            Check(1234.5.ToString("N1", L10n.Culture) == "1.234,5", "German numeric formatting missing");
            L10n.SetLocale("de_AT");
            Check(L10n.Get("tab.flight") == "FLUG" && L10n.Locale == "de-AT", "Regional German locale does not use German fallback");
            L10n.SetLocale("fr-FR");
            Check(L10n.Get("action.countdown") == "Start countdown", "Missing translation exposed a raw key");
            L10n.SetLocale(null);
            Check(L10n.Locale == "en-US", "Missing game locale did not fall back to English");
        }
        finally { CultureInfo.CurrentCulture = previous; Restore(); }
    }

    public static void CatalogIntegrity()
    {
        foreach (var entry in L10n.Catalog)
        {
            Check(!string.IsNullOrWhiteSpace(entry.Value.English) && !string.IsNullOrWhiteSpace(entry.Value.German), "Incomplete translation: " + entry.Key);
            string[] Slots(string text) => Regex.Matches(text, @"\{(\d+)(?:[^}]*)\}").Select(match => match.Groups[1].Value).Order().ToArray();
            Check(Slots(entry.Value.English).SequenceEqual(Slots(entry.Value.German)), "Translation changed numerical parameters: " + entry.Key);
            Check(!entry.Value.English.Contains("###") && !entry.Value.German.Contains("###"), "Text contains a widget ID: " + entry.Key);
        }
    }

    public static void CachedMessages()
    {
        try
        {
            var profile = new LaunchProfile();
            var prediction = new RoutePrediction { MessageKey = "preview.complete" };
            profile.SetStatus("status.orbit", 200.5, 199.5);
            L10n.SetLocale("en-US");
            Check(profile.StatusMessage.Contains("Orbit reached!") && profile.StatusMessage.Contains("200.5"), "English status missing");
            string english = prediction.Message;
            L10n.SetLocale("de-DE");
            Check(profile.StatusMessage.Contains("Orbit erreicht!") && profile.StatusMessage.Contains("200,5"), "Cached status did not follow language change");
            Check(prediction.Message != english && prediction.Message.StartsWith("Routenvorschau"), "Frozen route preview kept its previous language");
            profile.SetStatus("status.trim_wait", new LocalizedMessage("apsis.periapsis", Array.Empty<object>()), 30.0);
            L10n.SetLocale("en-US");
            Check(profile.StatusMessage.Contains("at periapsis"), "Nested apsis name kept its previous language");
            Check(MissionClock.Format(false, 10) == "T-00:00:10" && MissionClock.Format(true, 10) == "T+00:00:10", "Mission time depends on the language");
        }
        finally { Restore(); }
    }

    public static void NativeOverridesAndIds()
    {
        try
        {
            L10n.SetLocale("en-US");
            string englishId = L10n.Id("action.countdown");
            L10n.SetLocale("de-DE");
            string germanId = L10n.Id("action.countdown");
            Check(englishId[englishId.IndexOf("###")..] == germanId[germanId.IndexOf("###")..], "Language change altered the countdown button ID");
            L10n.SetLocale("fr-FR");
            L10n.ExternalTranslation = key => key == "action.countdown" ? "Démarrer le compte à rebours" : null;
            Check(L10n.Get("action.countdown") == "Démarrer le compte à rebours", "Game language pack did not override the fallback catalog");
            Check(L10n.Get("action.timer") == "T display", "Missing language-pack key has no fallback");
            L10n.ExternalTranslation = key => key == "status.orbit" ? "{4:F1}" : null;
            Check(L10n.Format("status.orbit", 200.5, 199.5).StartsWith("Orbit reached!"), "Malformed game translation interrupted status formatting");
        }
        finally { Restore(); }
    }
}
