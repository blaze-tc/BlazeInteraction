namespace Blaze.TestProviders.SharedDependency;

public static class DependencyVersion
{
    public static string Value => typeof(DependencyVersion).Assembly.GetName().Version!.ToString();
}
