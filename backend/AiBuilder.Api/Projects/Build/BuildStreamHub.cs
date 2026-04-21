using System.Collections.Concurrent;
using System.Threading.Channels;

namespace AiBuilder.Api.Projects.Build;

// In-memory fan-out for a single build run's output. Every line we write
// is:
//   1) appended to an in-memory buffer (so late SSE subscribers can catch up)
//   2) written to the on-disk transcript file (for history after restart)
//   3) pushed to every currently-subscribed channel for live tail
// When the run completes, all subscribers see a terminal event and the
// channels are closed.
public sealed class BuildStream
{
    public string RunId { get; }
    public string TranscriptPath { get; }
    public volatile bool Completed;
    public string? TerminalStatus; // "succeeded" | "failed" | null while running
    public string? TerminalDetail;

    private readonly List<string> _buffer = new();
    private readonly List<Channel<string>> _subscribers = new();
    private readonly object _lock = new();
    private readonly StreamWriter _fileWriter;

    public BuildStream(string runId, string transcriptPath)
    {
        RunId = runId;
        TranscriptPath = transcriptPath;
        Directory.CreateDirectory(Path.GetDirectoryName(transcriptPath)!);
        _fileWriter = new StreamWriter(
            new FileStream(transcriptPath, FileMode.Create, FileAccess.Write, FileShare.Read),
            leaveOpen: false) { AutoFlush = true };
    }

    public void Write(string line)
    {
        lock (_lock)
        {
            if (Completed) return;
            _buffer.Add(line);
            _fileWriter.WriteLine(line);
            foreach (var sub in _subscribers)
                sub.Writer.TryWrite(line);
        }
    }

    public void Complete(string status, string detail)
    {
        lock (_lock)
        {
            if (Completed) return;
            Completed = true;
            TerminalStatus = status;
            TerminalDetail = detail;
            _fileWriter.Flush();
            _fileWriter.Dispose();
            foreach (var sub in _subscribers)
                sub.Writer.TryComplete();
        }
    }

    // Subscribe returns the current buffer (snapshot) + a channel that will
    // receive future lines. If the run is already complete, the channel is
    // closed immediately so the reader just drains the backlog and exits.
    public (IReadOnlyList<string> Backlog, ChannelReader<string> Live, bool Completed, string? TerminalStatus, string? TerminalDetail) Subscribe()
    {
        lock (_lock)
        {
            var backlog = _buffer.ToArray();
            var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
            {
                SingleReader = true, SingleWriter = false,
            });
            if (Completed)
            {
                channel.Writer.TryComplete();
            }
            else
            {
                _subscribers.Add(channel);
            }
            return (backlog, channel.Reader, Completed, TerminalStatus, TerminalDetail);
        }
    }
}

public sealed class BuildStreamHub
{
    private readonly ConcurrentDictionary<string, BuildStream> _streams = new();

    public BuildStream Create(string runId, string transcriptPath) =>
        _streams.GetOrAdd(runId, id => new BuildStream(id, transcriptPath));

    public BuildStream? Get(string runId) =>
        _streams.TryGetValue(runId, out var s) ? s : null;
}
