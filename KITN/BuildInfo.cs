using System;
using System.Reflection;

namespace KITN
{
    internal static class BuildInfo
    {
        public const string RepositoryUrl = "https://github.com/steve-787/KITN";
        private static readonly Assembly Assembly = typeof(BuildInfo).Assembly;
        public static readonly string InformationalVersion = Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Assembly.GetName().Version?.ToString(3) ?? "unknown";
        private static readonly int RevisionSeparator = InformationalVersion.IndexOf('+');
        public static readonly string Version = RevisionSeparator < 0 ? InformationalVersion : InformationalVersion.Substring(0, RevisionSeparator);
        public static readonly string Commit = RevisionSeparator < 0 ? "unknown" : InformationalVersion.Substring(RevisionSeparator + 1);
        public static readonly string ShortCommit = Commit.Substring(0, Math.Min(7, Commit.Length));
        public static readonly string DisplayVersion = "v" + Version + " · " + ShortCommit;
    }
}
