// The .mcpr container writer.
// A .mcpr is a ZIP with exactly two entries:
//   * "recording.tmcpr": frames back to back, each an 8-byte big-endian header (int32 milliseconds since
// the recording started, int32 payload length) then the packet, itself a VarInt id then the body,
//   * "metaData.json": the fileFormat / fileFormatVersion / protocol / mcversion / duration / date /
// selfId / players / generator record ReplayMod reads.
//
// Frames arrive in both directions and AddPacket keeps only the clientbound ones, which is the format's rule.

using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DMCBK.OfficialPlugins.ReplayCapture;

/// <summary>The ReplayMod metadata record embedded in the archive.</summary>
public sealed class ReplayMetadata
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly SortedSet<string> _players = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The fixed metadata entry name inside the archive.</summary>
    public const string FileName = "metaData.json";

    /// <summary>Whether the recording is a single-player one. Always false for MCC.</summary>
    public bool SinglePlayer { get; set; }

    /// <summary>The "host:port" the recording was taken against.</summary>
    public string? ServerName { get; set; }

    /// <summary>An optional display name overriding <see cref="ServerName"/>.</summary>
    public string? CustomServerName { get; set; }

    /// <summary>The recording length in milliseconds.</summary>
    public int Duration { get; set; }

    /// <summary>The recording start, as Unix milliseconds.</summary>
    public long Date { get; set; }

    /// <summary>The Minecraft version name.</summary>
    public string McVersion { get; set; } = "0.0";

    /// <summary>The container marker ReplayMod checks.</summary>
    public string FileFormat { get; set; } = "MCPR";

    /// <summary>The container version ReplayMod checks.</summary>
    public int FileFormatVersion { get; set; } = 14;

    /// <summary>The protocol number the recording was taken with.</summary>
    public int Protocol { get; set; }

    /// <summary>The tool that produced the recording.</summary>
    public string Generator { get; set; } = "MCC";

    /// <summary>The recording client's own entity id, or -1 when unknown.</summary>
    public int SelfId { get; set; } = -1;

    /// <summary>Adds a player UUID to the recorded roster.</summary>
    public void AddPlayerUuid(Guid uuid) => _players.Add(uuid.ToString());

    /// <summary>The recorded player UUIDs.</summary>
    public IReadOnlyCollection<string> Players => _players;

    /// <summary>Serializes the metadata exactly as ReplayMod expects it.</summary>
    public string ToJson() => JsonSerializer.Serialize(
        new MetadataModel
        {
            Singleplayer = SinglePlayer,
            ServerName = ServerName,
            CustomServerName = CustomServerName,
            Duration = Duration,
            Date = Date,
            Mcversion = McVersion,
            FileFormat = FileFormat,
            FileFormatVersion = FileFormatVersion,
            Protocol = Protocol,
            Generator = Generator,
            SelfId = SelfId,
            Players = [.. _players],
        },
        JsonOptions);

    private sealed class MetadataModel
    {
        public bool Singleplayer { get; init; }

        public string? ServerName { get; init; }

        public string? CustomServerName { get; init; }

        public int Duration { get; init; }

        public long Date { get; init; }

        [JsonPropertyName("mcversion")]
        public string Mcversion { get; init; } = "0.0";

        public string FileFormat { get; init; } = "MCPR";

        public int FileFormatVersion { get; init; }

        public int Protocol { get; init; }

        public string Generator { get; init; } = "MCC";

        public int SelfId { get; init; } = -1;

        public string[] Players { get; init; } = [];
    }
}

/// <summary>The recording lifecycle states.</summary>
public enum ReplayRecordingState
{
    /// <summary>Frames are being appended.</summary>
    Recording,

    /// <summary>The archive is being written.</summary>
    Finalizing,

    /// <summary>The recording is closed.</summary>
    Stopped,
}

/// <summary>
/// Writes a ReplayMod .mcpr recording: frames go to a working <c>recording.tmcpr</c> stream, zipped with the metadata on <see cref="Stop"/> or <see cref="CreateBackup"/>.
/// </summary>
public sealed class ReplayWriter : IDisposable
{
    /// <summary>The fixed recording entry name inside the archive.</summary>
    public const string RecordingEntryName = "recording.tmcpr";

    private readonly object _gate = new();
    private readonly string _workingDirectory;
    private readonly string _recordingFilePath;
    private readonly FileStream _recordStream;
    private readonly DateTime _startUtc;
    private readonly string _instanceToken;
    private readonly TimeProvider _time;

    private ReplayRecordingState _state = ReplayRecordingState.Recording;
    private bool _recordStreamClosed;
    private bool _disposed;
    private DateTime _lastPacketUtc;

    /// <summary>Opens a recording under <paramref name="workingDirectory"/>, saving finished files to <paramref name="outputDirectory"/>.</summary>
    /// <param name="workingDirectory">Scratch folder for the in-progress tmcpr stream.</param>
    /// <param name="outputDirectory">Where finished .mcpr files are written.</param>
    /// <param name="protocolVersion">The negotiated protocol number.</param>
    /// <param name="minecraftVersion">The negotiated Minecraft version name.</param>
    /// <param name="serverName">The "host:port" of the recorded server.</param>
    /// <param name="time">The clock, injectable so the frame timing is testable.</param>
    public ReplayWriter(
        string workingDirectory,
        string outputDirectory,
        int protocolVersion,
        string minecraftVersion,
        string? serverName,
        TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        _time = time ?? TimeProvider.System;
        _workingDirectory = Path.GetFullPath(workingDirectory);
        OutputDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(_workingDirectory);
        Directory.CreateDirectory(OutputDirectory);

        _instanceToken = Path.GetRandomFileName().Replace(".", string.Empty, StringComparison.Ordinal);
        _recordingFilePath = Path.Combine(_workingDirectory, RecordingEntryName);
        _recordStream = new FileStream(_recordingFilePath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);

        _startUtc = _time.GetUtcNow().UtcDateTime;
        _lastPacketUtc = _startUtc;

        MinecraftVersion = minecraftVersion;
        Metadata = new ReplayMetadata
        {
            ServerName = serverName,
            Date = new DateTimeOffset(_startUtc, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            Protocol = protocolVersion,
            McVersion = minecraftVersion,
        };
    }

    /// <summary>Where finished .mcpr files are written.</summary>
    public string OutputDirectory { get; }

    /// <summary>The metadata that will be embedded in the archive.</summary>
    public ReplayMetadata Metadata { get; }

    /// <summary>The negotiated Minecraft version name, used in the default file name.</summary>
    public string MinecraftVersion { get; }

    /// <summary>The name of the last finished recording, or empty when none was written.</summary>
    public string ReplayFileName { get; private set; } = string.Empty;

    /// <summary>True while frames are still being appended.</summary>
    public bool RecordRunning
    {
        get
        {
            lock (_gate)
                return _state == ReplayRecordingState.Recording;
        }
    }

    /// <summary>How many frames have been appended.</summary>
    public int FrameCount { get; private set; }

    /// <summary>The client's own entity id; also written into the metadata.</summary>
    public void SetClientEntityId(int entityId)
    {
        lock (_gate)
        {
            if (entityId >= 0)
                Metadata.SelfId = entityId;
        }
    }

    /// <summary>Records a player UUID into the metadata roster.</summary>
    public void AddPlayerUuid(Guid uuid)
    {
        lock (_gate)
            Metadata.AddPlayerUuid(uuid);
    }

    /// <summary>
    /// Appends one clientbound frame.
    /// Serverbound frames are dropped: a replay is what the client saw.
    /// </summary>
    public void AddPacket(int packetId, ReadOnlySpan<byte> payload, bool isInbound = true)
    {
        if (!isInbound)
            return;

        lock (_gate)
        {
            if (_disposed || _state != ReplayRecordingState.Recording)
                return;

            _lastPacketUtc = _time.GetUtcNow().UtcDateTime;
            int elapsed = Math.Max(0, (int)(_lastPacketUtc - _startUtc).TotalMilliseconds);

            Span<byte> id = stackalloc byte[5];
            int idLength = WriteVarInt(id, packetId);

            Span<byte> header = stackalloc byte[8];
            BinaryPrimitives.WriteInt32BigEndian(header, elapsed);
            BinaryPrimitives.WriteInt32BigEndian(header[4..], idLength + payload.Length);

            _recordStream.Write(header);
            _recordStream.Write(id[..idLength]);
            _recordStream.Write(payload);
            FrameCount++;
        }
    }

    /// <summary>Writes a snapshot archive while the recording keeps running.</summary>
    public string CreateBackup(string fileName)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state != ReplayRecordingState.Recording)
                return string.Empty;

            string path = ResolvePath(fileName);
            WriteArchive(path, fromActiveStream: true);
            return path;
        }
    }

    /// <summary>Stops the recording and writes the final archive. Returns its path, or empty when already stopped.</summary>
    public string Stop()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state != ReplayRecordingState.Recording)
                return string.Empty;

            string fileName = GetDefaultFileName();
            string path = ResolvePath(fileName);
            _state = ReplayRecordingState.Finalizing;
            try
            {
                CloseRecordStream();
                WriteArchive(path, fromActiveStream: false);
                ReplayFileName = fileName;
                return path;
            }
            finally
            {
                _state = ReplayRecordingState.Stopped;
                CleanupWorkingFiles();
            }
        }
    }

    /// <summary>The unique default file name for this recording.</summary>
    public string GetDefaultFileName()
    {
        string version = MinecraftVersion.Replace('.', '_');
        string stamp = _time.GetUtcNow().UtcDateTime.ToString("yyyy_MM_dd_HH_mm_ss_fff", CultureInfo.InvariantCulture);
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0}_{1}_{2}_{3}.mcpr",
            stamp,
            version,
            Environment.ProcessId,
            _instanceToken);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;

        try
        {
            if (RecordRunning)
                Stop();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A recording that cannot be finalized must not take the client down on unload.
        }
        finally
        {
            lock (_gate)
            {
                CloseRecordStream();
                _disposed = true;
            }
        }
    }

    /// <summary>Writes an unsigned VarInt; returns the number of bytes written.</summary>
    public static int WriteVarInt(Span<byte> destination, int value)
    {
        uint remaining = (uint)value;
        int written = 0;
        while (true)
        {
            if ((remaining & ~0x7Fu) == 0)
            {
                destination[written++] = (byte)remaining;
                return written;
            }

            destination[written++] = (byte)((remaining & 0x7F) | 0x80);
            remaining >>= 7;
        }
    }

    private void WriteArchive(string path, bool fromActiveStream)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        Metadata.Duration = Math.Max(0, (int)(_lastPacketUtc - _startUtc).TotalMilliseconds);

        using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);

        using (Stream entry = archive.CreateEntry(RecordingEntryName).Open())
        {
            if (fromActiveStream)
            {
                _recordStream.Flush();
                long position = _recordStream.Position;
                try
                {
                    _recordStream.Position = 0;
                    _recordStream.CopyTo(entry);
                }
                finally
                {
                    _recordStream.Position = position;
                }
            }
            else
            {
                using var source = new FileStream(_recordingFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                source.CopyTo(entry);
            }
        }

        using Stream metadataEntry = archive.CreateEntry(ReplayMetadata.FileName).Open();
        using var writer = new StreamWriter(metadataEntry);
        writer.Write(Metadata.ToJson());
    }

    private string ResolvePath(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        return Path.IsPathRooted(fileName) || !string.IsNullOrEmpty(Path.GetDirectoryName(fileName))
            ? fileName
            : Path.Combine(OutputDirectory, fileName);
    }

    private void CloseRecordStream()
    {
        if (_recordStreamClosed)
            return;

        _recordStream.Flush();
        _recordStream.Dispose();
        _recordStreamClosed = true;
    }

    private void CleanupWorkingFiles()
    {
        try
        {
            if (File.Exists(_recordingFilePath))
                File.Delete(_recordingFilePath);

            if (Directory.Exists(_workingDirectory) && Directory.GetFileSystemEntries(_workingDirectory).Length == 0)
                Directory.Delete(_workingDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leftover scratch files are harmless.
        }
    }
}
