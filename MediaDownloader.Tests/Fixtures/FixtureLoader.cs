namespace MediaDownloader.Tests.Fixtures;

internal static class FixtureLoader
{
    public static string Load(string fileName) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName));
}
