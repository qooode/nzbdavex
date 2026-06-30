namespace NzbWebDAV.Streams;

internal static class StreamSeek
{
    public static long Resolve(long currentPosition, long length, long offset, SeekOrigin origin)
    {
        long absoluteOffset;
        try
        {
            absoluteOffset = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(currentPosition + offset),
                SeekOrigin.End => checked(length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin), origin, null)
            };
        }
        catch (OverflowException)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "Seek position is outside stream bounds.");
        }

        if (absoluteOffset < 0 || absoluteOffset > length)
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "Seek position is outside stream bounds.");

        return absoluteOffset;
    }
}
