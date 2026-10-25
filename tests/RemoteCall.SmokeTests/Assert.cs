namespace RemoteCall.SmokeTests;

internal sealed class AssertionException : Exception
{
    public AssertionException(string message) : base(message)
    {
    }
}

internal static class Assert
{
    public static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new AssertionException(message);
        }
    }

    public static void Equal<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new AssertionException((message ?? "值不相等") + $"，期望：{expected}，实际：{actual}");
        }
    }

    public static void Contains(string haystack, string needle, string message)
    {
        if (!haystack.Contains(needle, StringComparison.Ordinal))
        {
            throw new AssertionException($"{message}（找不到「{needle}」，实际内容前 200 字：{Truncate(haystack, 200)}）");
        }
    }

    public static void NotContains(string haystack, string needle, string message)
    {
        if (haystack.Contains(needle, StringComparison.Ordinal))
        {
            throw new AssertionException($"{message}（不应出现「{needle}」）");
        }
    }

    public static string Truncate(string value, int length)
    {
        return value.Length <= length ? value : value[..length] + "…";
    }
}
