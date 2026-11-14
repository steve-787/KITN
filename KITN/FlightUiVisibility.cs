namespace KITN
{
    internal static class FlightUiVisibility
    {
        public static bool ShouldShow(bool gameLoaded, bool editorOpen, bool hasRocket) => gameLoaded && !editorOpen && hasRocket;
    }
}
