using NxHub.Core.Errors;
using NxHub.Core.Sessions;
using NxHub.Protocol.SysBotBase;

namespace NxHub.Core.Screen;

/// <summary>One captured frame of the Switch screen.</summary>
public sealed record ScreenFrame(byte[] Jpeg, DateTimeOffset CapturedAt, long Sequence, double CaptureMs);

/// <summary>
/// Screenshots for one Switch ("live view"). Viewers ask for a frame no older than some age;
/// concurrent requests share one capture, so any number of viewers cost the Switch one
/// <c>pixelPeek</c> at a time.
/// </summary>
/// <remarks>
/// Captures run on their own token, never the viewer's: cancelling a request half way through a
/// response would force the connection to be discarded (see <see cref="SysBotBaseClient"/>).
/// </remarks>
public sealed class ScreenCapture
{
    private static readonly byte[] JpegMagic = [0xFF, 0xD8, 0xFF];

    private readonly SwitchSession _session;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private ScreenFrame? _latest;
    private Task<ScreenFrame>? _inFlight;
    private long _sequence;

    internal ScreenCapture(SwitchSession session, TimeProvider time)
    {
        _session = session;
        _time = time;
    }

    public ScreenFrame? Latest
    {
        get
        {
            lock (_gate)
            {
                return _latest;
            }
        }
    }

    /// <summary>Returns a frame captured within <paramref name="maxAge"/>, capturing a new one if needed.</summary>
    public async Task<ScreenFrame> GetFrameAsync(TimeSpan maxAge, CancellationToken cancellationToken)
    {
        Task<ScreenFrame> capture;
        lock (_gate)
        {
            if (_latest is { } latest && _time.GetUtcNow() - latest.CapturedAt <= maxAge)
            {
                return latest;
            }

            _inFlight ??= Task.Run(CaptureAsync, CancellationToken.None);
            capture = _inFlight;
        }

        return await capture.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<ScreenFrame> CaptureAsync()
    {
        try
        {
            if (_session.GetStatus().Capabilities is { Screenshot: false })
            {
                throw Unavailable("This sys-botbase version does not support screenshots.");
            }

            var started = _time.GetTimestamp();
            var reply = await _session.ExecuteAsync((client, ct) => client.QueryAsync(SysBotCommands.PixelPeek(), ct), CancellationToken.None).ConfigureAwait(false);
            var elapsed = _time.GetElapsedTime(started);

            byte[] jpeg;
            try
            {
                jpeg = Convert.FromHexString(reply.Line.Trim());
            }
            catch (FormatException)
            {
                throw Unavailable("The Switch returned something that is not an image.");
            }

            if (jpeg.Length < JpegMagic.Length || !jpeg.AsSpan(0, JpegMagic.Length).SequenceEqual(JpegMagic))
            {
                throw Unavailable(
                    "The Switch returned no image. sys-botbase screenshots can fail unless the console's debug-unit flag is patched; everything else keeps working.");
            }

            var frame = new ScreenFrame(jpeg, _time.GetUtcNow(), Interlocked.Increment(ref _sequence), Math.Round(elapsed.TotalMilliseconds));
            lock (_gate)
            {
                _latest = frame;
            }

            return frame;
        }
        finally
        {
            lock (_gate)
            {
                _inFlight = null;
            }
        }
    }

    private static NxHubException Unavailable(string message) =>
        new(ErrorKind.Unsupported, "screenshot_unavailable", message);
}
