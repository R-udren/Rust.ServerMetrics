namespace RustServerMetrics;

internal static class UploadPolicy
{
    public const int BufferCapacity = 100000;
    public const int MaximumAttempts = 3;
    public const float CoalesceSeconds = 1f;

    public static bool ShouldCoalesce(int queuedReports, int batchSize) => queuedReports < batchSize;

    public static bool ShouldRetry(bool networkError, long httpStatus, int attempt) =>
        attempt < MaximumAttempts && (networkError || httpStatus == 429 || httpStatus >= 500);
}
