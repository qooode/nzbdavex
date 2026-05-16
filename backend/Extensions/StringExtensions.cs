namespace NzbWebDAV.Extensions;

public static class StringExtensions
{
    public static bool IsAny(this string value, params string[] acceptedValues)
    {
        return acceptedValues.Any(acceptedValue => value == acceptedValue);
    }

    public static string RemovePrefix(this string value, string prefix)
    {
        return value.StartsWith(prefix) ? value[prefix.Length..] : value;
    }
}