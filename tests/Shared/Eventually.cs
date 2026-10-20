namespace NxHub.Tests.Shared;

internal static class Eventually
{
    /// <summary>Polls a condition in real time until it holds or the timeout elapses.</summary>
    public static async Task TrueAsync(Func<bool> condition, string because, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Condition not met within timeout: {because}");
            }

            await Task.Delay(10);
        }
    }

    public static async Task TrueAsync(Func<Task<bool>> condition, string because, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Condition not met within timeout: {because}");
            }

            await Task.Delay(25);
        }
    }
}
