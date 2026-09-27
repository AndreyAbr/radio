using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;

namespace RadioServer.Stations;

/// <summary>
/// Представляет отдельную радиостанцию с собственным плейлистом и слушателями.
/// Воспроизведение треков непрерывно идет на сервере в реальном времени, 
/// а все подключенные слушатели транслируются в единый живой эфир.
/// </summary>
public class RadioStation : IDisposable
{
    private readonly object _lock = new();
    private readonly object _burstLock = new();

    private int _listenersCount = 0;
    private int _currentTrackIndex = 0;
    private int _trackVersion = 1;
    private CancellationTokenSource _skipCts = new();
    private List<string> _tracks = new();

    // Подписчики живого эфира: каждый слушатель получает порции из Channel
    private readonly ConcurrentDictionary<Guid, Channel<ReadOnlyMemory<byte>>> _subscribers = new();

    // Кольцевой буфер последних фрагментов эфира (~48-64 КБ) для мгновенного захвата потока браузером
    private readonly LinkedList<byte[]> _burstChunks = new();
    private int _burstBytesTotal = 0;
    private const int MaxBurstBytes = 32 * 1024; // ~32 КБ для моментального старта воспроизведения в плеере с минимальной задержкой

    // Фоновая задача непрерывного серверного вещания
    private readonly CancellationTokenSource _stationLifetimeCts = new();
    private readonly Task _broadcastTask;

    public string Id { get; }
    public string Name { get; }
    public string DirectoryPath { get; }

    private long _trackDurationMs = 0;
    private long _trackRemainingMs = 0;

    private volatile bool _isPaused = false;
    private volatile bool _isLiveDj = false;
    private readonly object _seekLock = new();
    private double? _pendingSeekSeconds = null;

    private CancellationTokenSource _liveDjWakeupCts = new();
    private long _lastLiveDjTimestamp = 0;

    // Кэшированный буфер тишины для паузы (10 фреймов MPEG-1 Layer 3 ~261 мс тишины)
    private static readonly byte[] CachedSilenceBuffer = CreateSilenceBuffer(10);
    // Буфер тишины для мгновенного заполнения burst-буфера при паузе (150 фреймов ~62 КБ)
    private static readonly byte[] CachedSilenceBurstBuffer = CreateSilenceBuffer(150);

    public int ListenersCount => Volatile.Read(ref _listenersCount);
    public int? MaxListeners { get; set; } = null;
    public bool CanAcceptListener => !MaxListeners.HasValue || ListenersCount < MaxListeners.Value;
    public int CurrentTrackIndex { get { lock (_lock) return _currentTrackIndex; } }
    public int TrackVersion { get { lock (_lock) return _trackVersion; } }
    public int TrackDurationSeconds => (int)(Interlocked.Read(ref _trackDurationMs) / 1000);
    public int TrackRemainingSeconds => (int)Math.Max(0, Interlocked.Read(ref _trackRemainingMs) / 1000);
    public bool IsPaused => _isPaused;
    public bool IsLiveDj => _isLiveDj;

    public void Pause()
    {
        _isPaused = true;
        lock (_burstLock)
        {
            _burstChunks.Clear();
            _burstBytesTotal = 0;
            AppendBurstChunk((byte[])CachedSilenceBurstBuffer.Clone());
        }
    }

    public void Resume()
    {
        _isPaused = false;
        // Не очищаем _burstChunks в 0, чтобы новые слушатели не получали пустой буфер
    }

    public bool TogglePause()
    {
        _isPaused = !_isPaused;
        lock (_burstLock)
        {
            if (_isPaused)
            {
                _burstChunks.Clear();
                _burstBytesTotal = 0;
                AppendBurstChunk((byte[])CachedSilenceBurstBuffer.Clone());
            }
        }
        return _isPaused;
    }

    public bool SeekTo(double targetSeconds)
    {
        lock (_seekLock)
        {
            _pendingSeekSeconds = Math.Max(0, targetSeconds);
            long durationMs = Interlocked.Read(ref _trackDurationMs);
            if (durationMs > 0)
            {
                long remainingMs = Math.Max(0, durationMs - (long)(targetSeconds * 1000));
                Interlocked.Exchange(ref _trackRemainingMs, remainingMs);
            }
        }
        return true;
    }

    public bool SeekBy(double deltaSeconds)
    {
        long durationMs = Interlocked.Read(ref _trackDurationMs);
        long remainingMs = Interlocked.Read(ref _trackRemainingMs);
        double currentElapsedSec = Math.Max(0, (durationMs - remainingMs) / 1000.0);
        double target = Math.Clamp(currentElapsedSec + deltaSeconds, 0, durationMs > 0 ? durationMs / 1000.0 : 0);
        return SeekTo(target);
    }

    public void SetLiveDj(bool active)
    {
        _isLiveDj = active;
        if (active)
        {
            Volatile.Write(ref _lastLiveDjTimestamp, Stopwatch.GetTimestamp());

            // Сбрасываем накопившиеся очереди у активных подписчиков,
            // чтобы голос ведущего звучал моментально без задержки старого буфера музыки
            foreach (var sub in _subscribers.Values)
            {
                while (sub.Reader.TryRead(out _)) { }
            }

            // Очищаем также burst-буфер, чтобы новые слушатели сразу получали голос ведущего, а не старый трек
            lock (_burstLock)
            {
                _burstChunks.Clear();
                _burstBytesTotal = 0;
            }
        }

        // Прерываем ожидание в цикле вещания, чтобы сервер мгновенно переключился
        try
        {
            _liveDjWakeupCts.Cancel();
            _liveDjWakeupCts.Dispose();
        }
        catch { }
        _liveDjWakeupCts = new CancellationTokenSource();
    }

    public void PushLiveAudio(ReadOnlyMemory<byte> chunk)
    {
        _isLiveDj = true;
        Volatile.Write(ref _lastLiveDjTimestamp, Stopwatch.GetTimestamp());

        lock (_burstLock)
        {
            AppendBurstChunk(chunk.ToArray());
        }

        foreach (var sub in _subscribers.Values)
        {
            sub.Writer.TryWrite(chunk);
        }
    }

    private static byte[] CreateSilenceBuffer(int frameCount)
    {
        const int frameLen = 417; // 128 kbps, 44.1 kHz, Joint Stereo
        var result = new byte[frameLen * frameCount];
        for (int i = 0; i < frameCount; i++)
        {
            int offset = i * frameLen;
            result[offset] = 0xFF;
            result[offset + 1] = 0xFB; // MPEG-1 Layer 3, no CRC
            result[offset + 2] = 0x90; // 128 kbps, 44100 Hz
            result[offset + 3] = 0x64; // Joint stereo
        }
        return result;
    }

    public RadioStation(string id, string name, string directoryPath)
    {
        Id = id;
        Name = name;
        DirectoryPath = directoryPath;
        RefreshPlaylist();

        // Предустанавливаем буфер тишины, чтобы первый подключившийся слушатель не ждал наполнения буфера
        lock (_burstLock)
        {
            AppendBurstChunk((byte[])CachedSilenceBurstBuffer.Clone());
        }

        // Запуск фонового непрерывного эфира на сервере
        _broadcastTask = Task.Run(BroadcastLoopAsync);
    }

    /// <summary>
    /// Перечитывает список MP3/OGG-файлов из директории станции.
    /// </summary>
    public void RefreshPlaylist()
    {
        lock (_lock)
        {
            if (!Directory.Exists(DirectoryPath))
            {
                Directory.CreateDirectory(DirectoryPath);
            }

            var files = Directory.GetFiles(DirectoryPath, "*.mp3", SearchOption.TopDirectoryOnly)
                .Concat(Directory.GetFiles(DirectoryPath, "*.ogg", SearchOption.TopDirectoryOnly))
                .Where(File.Exists)
                .OrderBy(f => f)
                .ToList();

            _tracks = files;
            if (_tracks.Count == 0)
            {
                _currentTrackIndex = 0;
            }
            else if (_currentTrackIndex >= _tracks.Count)
            {
                _currentTrackIndex = 0;
            }
        }
    }

    /// <summary>
    /// Попытка подключения клиента к живому эфиру станции с учетом лимита слушателей.
    /// </summary>
    public (bool Success, Guid SubId, ChannelReader<ReadOnlyMemory<byte>> Reader, byte[] InitialBurst) TrySubscribe()
    {
        if (MaxListeners.HasValue)
        {
            while (true)
            {
                int current = Volatile.Read(ref _listenersCount);
                if (current >= MaxListeners.Value)
                {
                    return (false, Guid.Empty, null!, Array.Empty<byte>());
                }
                if (Interlocked.CompareExchange(ref _listenersCount, current + 1, current) == current)
                {
                    break;
                }
            }
        }
        else
        {
            Interlocked.Increment(ref _listenersCount);
        }

        var subId = Guid.NewGuid();
        var channel = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(16)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });

        _subscribers[subId] = channel;

        byte[] burst;
        lock (_burstLock)
        {
            if (_isPaused)
            {
                burst = (byte[])CachedSilenceBurstBuffer.Clone();
            }
            else
            {
                burst = CombineBurstChunks();
                if (burst.Length < 16 * 1024)
                {
                    // Дополняем валидными фреймами тишины спереди для моментального старта воспроизведения в плеере (~24 КБ)
                    int neededSilence = Math.Max(0, 24 * 1024 - burst.Length);
                    int silenceCount = Math.Max(1, neededSilence / 417);
                    byte[] silencePad = CreateSilenceBuffer(silenceCount);
                    byte[] padded = new byte[silencePad.Length + burst.Length];
                    Buffer.BlockCopy(silencePad, 0, padded, 0, silencePad.Length);
                    Buffer.BlockCopy(burst, 0, padded, silencePad.Length, burst.Length);
                    burst = padded;
                }
            }
        }

        return (true, subId, channel.Reader, burst);
    }

    /// <summary>
    /// Подключение клиента к живому эфиру станции.
    /// Возвращает стартовый срез эфира (burst) и канал для получения живых пакетов в реальном времени.
    /// </summary>
    public (Guid SubId, ChannelReader<ReadOnlyMemory<byte>> Reader, byte[] InitialBurst) Subscribe()
    {
        var (_, subId, reader, burst) = TrySubscribe();
        return (subId, reader, burst);
    }

    /// <summary>
    /// Отключение клиента и освобождение ресурсов канала.
    /// </summary>
    public void Unsubscribe(Guid subId)
    {
        if (_subscribers.TryRemove(subId, out var channel))
        {
            channel.Writer.TryComplete();
            Interlocked.Decrement(ref _listenersCount);
        }
    }

    public int AddListener() => Interlocked.Increment(ref _listenersCount);

    public int RemoveListener()
    {
        var count = Interlocked.Decrement(ref _listenersCount);
        if (count < 0)
        {
            Interlocked.CompareExchange(ref _listenersCount, 0, count);
            return 0;
        }
        return count;
    }

    /// <summary>
    /// Возвращает имя текущего воспроизводимого трека.
    /// </summary>
    public string GetCurrentTrackName()
    {
        lock (_lock)
        {
            if (_tracks.Count == 0)
            {
                RefreshPlaylist();
                if (_tracks.Count == 0)
                    return "Эфир пуст (нет треков)";
            }
            var idx = _currentTrackIndex % _tracks.Count;
            return Path.GetFileName(_tracks[idx]);
        }
    }

    /// <summary>
    /// Количество треков в плейлисте.
    /// </summary>
    public int TrackCount
    {
        get
        {
            lock (_lock) return _tracks.Count;
        }
    }

    /// <summary>
    /// Возвращает имена всех треков в плейлисте станции.
    /// </summary>
    public IReadOnlyList<string> GetTracks()
    {
        lock (_lock)
        {
            return _tracks.Select(Path.GetFileName).Where(f => f != null).Cast<string>().ToList();
        }
    }

    /// <summary>
    /// Удаляет трек из плейлиста станции и с диска.
    /// </summary>
    public bool DeleteTrack(string fileName)
    {
        lock (_lock)
        {
            var safeName = Path.GetFileName(fileName);
            var targetPath = Path.Combine(DirectoryPath, safeName);
            if (!File.Exists(targetPath))
                return false;

            try
            {
                // Если удаляется текущий трек и в плейлисте несколько треков, переключаем на следующий
                if (_tracks.Count > 1 && _currentTrackIndex >= 0 && _currentTrackIndex < _tracks.Count &&
                    Path.GetFileName(_tracks[_currentTrackIndex]).Equals(safeName, StringComparison.OrdinalIgnoreCase))
                {
                    SkipTrack();
                }

                File.Delete(targetPath);
                RefreshPlaylist();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Принудительно переключает станцию на следующий трек в живом эфире.
    /// </summary>
    public string SkipTrack()
    {
        lock (_lock)
        {
            if (_tracks.Count == 0)
            {
                RefreshPlaylist();
                if (_tracks.Count == 0)
                    return "Эфир пуст";
            }

            _currentTrackIndex = (_currentTrackIndex + 1) % _tracks.Count;
            _trackVersion++;

            // Прерываем текущий трек в вещателе
            try
            {
                _skipCts.Cancel();
                _skipCts.Dispose();
            }
            catch { }
            _skipCts = new CancellationTokenSource();

            lock (_burstLock)
            {
                _burstChunks.Clear();
                _burstBytesTotal = 0;
                AppendBurstChunk((byte[])CachedSilenceBurstBuffer.Clone());
            }

            Interlocked.Exchange(ref _trackRemainingMs, 0);
            Interlocked.Exchange(ref _trackDurationMs, 0);
            _isPaused = false;
            lock (_seekLock) { _pendingSeekSeconds = null; }

            return Path.GetFileName(_tracks[_currentTrackIndex]);
        }
    }

    /// <summary>
    /// Совместимость со старыми тестами: возвращает трек и версию.
    /// </summary>
    public (string? FilePath, int TrackIndex, int TrackVersion, CancellationToken SkipToken) GetTrackForClient(int clientTrackIndex, int clientTrackVersion)
    {
        lock (_lock)
        {
            if (_tracks.Count == 0)
            {
                RefreshPlaylist();
                if (_tracks.Count == 0)
                    return (null, 0, _trackVersion, _skipCts.Token);
            }

            if (clientTrackVersion != _trackVersion || clientTrackIndex < 0 || clientTrackIndex >= _tracks.Count)
            {
                clientTrackIndex = _currentTrackIndex;
            }

            var filePath = _tracks[clientTrackIndex % _tracks.Count];
            if (!File.Exists(filePath))
            {
                RefreshPlaylist();
                if (_tracks.Count == 0)
                    return (null, 0, _trackVersion, _skipCts.Token);

                clientTrackIndex = _currentTrackIndex % _tracks.Count;
                filePath = _tracks[clientTrackIndex];
            }

            return (filePath, clientTrackIndex, _trackVersion, _skipCts.Token);
        }
    }

    /// <summary>
    /// Совместимость со старыми тестами.
    /// </summary>
    public void OnTrackFinished(int trackIndex, int trackVersion)
    {
        lock (_lock)
        {
            if (trackVersion == _trackVersion && trackIndex == _currentTrackIndex)
            {
                if (_tracks.Count > 0)
                {
                    _currentTrackIndex = (_currentTrackIndex + 1) % _tracks.Count;
                    _trackVersion++;
                }
            }
        }
    }

    public StationInfo ToInfo()
    {
        return new StationInfo(
            Id,
            Name,
            ListenersCount,
            GetCurrentTrackName(),
            TrackDurationSeconds,
            TrackRemainingSeconds,
            IsPaused,
            IsLiveDj,
            MaxListeners
        );
    }

    /// <summary>
    /// Основной серверный цикл непрерывного вещания в реальном времени.
    /// </summary>
    private async Task BroadcastLoopAsync()
    {
        var buffer = new byte[8192];
        var lifetimeToken = _stationLifetimeCts.Token;

        while (!lifetimeToken.IsCancellationRequested)
        {
            // Прямой эфир DJ работает даже если плейлист пуст
            if (_isLiveDj)
            {
                long lastTs = Volatile.Read(ref _lastLiveDjTimestamp);
                long elapsedSinceLiveMs = (Stopwatch.GetTimestamp() - lastTs) * 1000 / Stopwatch.Frequency;
                if (elapsedSinceLiveMs > 500 && !_subscribers.IsEmpty)
                {
                    ReadOnlyMemory<byte> silence = CachedSilenceBuffer;
                    foreach (var sub in _subscribers.Values)
                    {
                        sub.Writer.TryWrite(silence);
                    }
                }

                try
                {
                    using var linkedLive = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken, _liveDjWakeupCts.Token);
                    await Task.Delay(150, linkedLive.Token);
                }
                catch (OperationCanceledException) { }
                continue;
            }

            string? filePath = null;
            CancellationToken skipToken;

            lock (_lock)
            {
                if (_tracks.Count == 0)
                    RefreshPlaylist();

                if (_tracks.Count > 0)
                {
                    _currentTrackIndex %= _tracks.Count;
                    filePath = _tracks[_currentTrackIndex];
                }
                skipToken = _skipCts.Token;
            }

            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                // Транслируем тишину подключенным слушателям, чтобы соединение не рвалось
                if (!_subscribers.IsEmpty)
                {
                    ReadOnlyMemory<byte> silence = CachedSilenceBuffer;
                    foreach (var sub in _subscribers.Values)
                    {
                        sub.Writer.TryWrite(silence);
                    }
                }
                try
                {
                    await Task.Delay(250, lifetimeToken);
                }
                catch (OperationCanceledException) { break; }
                continue;
            }

            var trackInfo = ParseMp3TrackInfo(filePath);
            int bitrate = trackInfo.Bitrate > 0 ? trackInfo.Bitrate : 128000;
            int bytesPerSecond = Math.Max(8000, bitrate / 8);
            const int ticksPerSec = 4; // 4 чанка в секунду (каждые 250 мс)
            int chunkSize = bytesPerSecond / ticksPerSec;
            if (chunkSize > buffer.Length)
                buffer = new byte[chunkSize];

            TimeSpan interval = TimeSpan.FromMilliseconds(1000.0 / ticksPerSec);

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken, skipToken);

            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

                if (trackInfo.AudioOffset > 0 && trackInfo.AudioOffset < fs.Length)
                {
                    fs.Seek(trackInfo.AudioOffset, SeekOrigin.Begin);
                }

                long totalAudioBytes = trackInfo.AudioLength > 0 ? trackInfo.AudioLength : Math.Max(1, fs.Length - fs.Position);
                long totalStreamed = 0;
                long totalDurationMs = trackInfo.DurationMs > 0
                    ? trackInfo.DurationMs
                    : (long)((double)totalAudioBytes / bytesPerSecond * 1000);

                Interlocked.Exchange(ref _trackDurationMs, totalDurationMs);
                Interlocked.Exchange(ref _trackRemainingMs, totalDurationMs);

                while (!linkedCts.Token.IsCancellationRequested)
                {
                    long startTimestamp = Stopwatch.GetTimestamp();

                    // 1. Проверяем режим прямого эфира Live DJ (микрофон/микшер)
                    if (_isLiveDj)
                    {
                        long lastTs = Volatile.Read(ref _lastLiveDjTimestamp);
                        long elapsedSinceLiveMs = (Stopwatch.GetTimestamp() - lastTs) * 1000 / Stopwatch.Frequency;
                        if (elapsedSinceLiveMs > 500 && !_subscribers.IsEmpty)
                        {
                            ReadOnlyMemory<byte> silence = CachedSilenceBuffer;
                            foreach (var sub in _subscribers.Values)
                            {
                                sub.Writer.TryWrite(silence);
                            }
                        }

                        try
                        {
                            using var linkedLive = CancellationTokenSource.CreateLinkedTokenSource(linkedCts.Token, _liveDjWakeupCts.Token);
                            await Task.Delay(150, linkedLive.Token);
                        }
                        catch (OperationCanceledException) { }
                        continue;
                    }

                    // 2. Проверяем запрос перемотки трека (Seek)
                    double? seekTarget = null;
                    lock (_seekLock)
                    {
                        if (_pendingSeekSeconds.HasValue)
                        {
                            seekTarget = _pendingSeekSeconds;
                            _pendingSeekSeconds = null;
                        }
                    }

                    if (seekTarget.HasValue)
                    {
                        long targetByteOffset = (long)(seekTarget.Value * bytesPerSecond);
                        targetByteOffset = Math.Clamp(targetByteOffset, 0, totalAudioBytes);
                        long targetFilePos = trackInfo.AudioOffset + targetByteOffset;

                        // Выравниваем позицию чтения на начало валидного MPEG-фрейма
                        if (TryFindNextFrame(fs, targetFilePos, fs.Length, out long alignedPos, out _))
                        {
                            fs.Seek(alignedPos, SeekOrigin.Begin);
                            totalStreamed = Math.Clamp(alignedPos - trackInfo.AudioOffset, 0, totalAudioBytes);
                        }
                        else
                        {
                            fs.Seek(targetFilePos, SeekOrigin.Begin);
                            totalStreamed = targetByteOffset;
                        }

                        long seekRemainingMs = Math.Max(0, totalDurationMs - (long)(seekTarget.Value * 1000));
                        Interlocked.Exchange(ref _trackRemainingMs, seekRemainingMs);

                        lock (_burstLock)
                        {
                            _burstChunks.Clear();
                            _burstBytesTotal = 0;
                            if (_isPaused)
                            {
                                AppendBurstChunk((byte[])CachedSilenceBurstBuffer.Clone());
                            }
                            else
                            {
                                PopulateBurstFromTrack(fs, trackInfo, totalStreamed);
                            }
                        }
                    }

                    // 3. Проверяем паузу воспроизведения
                    if (_isPaused)
                    {
                        ReadOnlyMemory<byte> silence = CachedSilenceBuffer;
                        foreach (var sub in _subscribers.Values)
                        {
                            sub.Writer.TryWrite(silence);
                        }

                        lock (_burstLock)
                        {
                            if (_burstBytesTotal < MaxBurstBytes)
                            {
                                AppendBurstChunk(CachedSilenceBuffer);
                            }
                        }

                        var pauseElapsed = Stopwatch.GetElapsedTime(startTimestamp);
                        var pauseRemaining = interval - pauseElapsed;
                        if (pauseRemaining > TimeSpan.Zero)
                        {
                            await Task.Delay(pauseRemaining, linkedCts.Token);
                        }
                        continue;
                    }

                    int toRead = chunkSize;
                    if (totalAudioBytes > totalStreamed && (totalAudioBytes - totalStreamed) < toRead)
                    {
                        toRead = (int)(totalAudioBytes - totalStreamed);
                    }

                    int bytesRead = await fs.ReadAsync(buffer.AsMemory(0, toRead), linkedCts.Token);
                    if (bytesRead <= 0)
                    {
                        // Трек закончился — переходим к следующему
                        lock (_lock)
                        {
                            if (_tracks.Count > 0)
                            {
                                _currentTrackIndex = (_currentTrackIndex + 1) % _tracks.Count;
                                _trackVersion++;
                            }
                        }
                        Interlocked.Exchange(ref _trackRemainingMs, 0);
                        break;
                    }

                    totalStreamed += bytesRead;
                    double remainingProgress = Math.Clamp(1.0 - ((double)totalStreamed / totalAudioBytes), 0.0, 1.0);
                    long remainingMs = (long)(totalDurationMs * remainingProgress);
                    Interlocked.Exchange(ref _trackRemainingMs, remainingMs);

                    byte[] chunk = new byte[bytesRead];
                    Buffer.BlockCopy(buffer, 0, chunk, 0, bytesRead);

                    // Сохраняем в срез burst-буфера
                    lock (_burstLock)
                    {
                        AppendBurstChunk(chunk);
                    }

                    // Транслируем всем подключенным слушателям напрямую
                    ReadOnlyMemory<byte> mem = chunk;
                    foreach (var sub in _subscribers.Values)
                    {
                        sub.Writer.TryWrite(mem);
                    }

                    // Точная выдержка темпа (pacing в реальном времени)
                    var elapsed = Stopwatch.GetElapsedTime(startTimestamp);
                    var remaining = interval - elapsed;
                    if (remaining > TimeSpan.Zero)
                    {
                        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(linkedCts.Token, _liveDjWakeupCts.Token);
                        try
                        {
                            await Task.Delay(remaining, delayCts.Token);
                        }
                        catch (OperationCanceledException) when (_liveDjWakeupCts.IsCancellationRequested && !linkedCts.IsCancellationRequested)
                        {
                            continue;
                        }
                    }

                    if (totalStreamed >= totalAudioBytes)
                    {
                        lock (_lock)
                        {
                            if (_tracks.Count > 0)
                            {
                                _currentTrackIndex = (_currentTrackIndex + 1) % _tracks.Count;
                                _trackVersion++;
                            }
                        }
                        Interlocked.Exchange(ref _trackRemainingMs, 0);
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (skipToken.IsCancellationRequested && !lifetimeToken.IsCancellationRequested)
            {
                // Принудительный пропуск трека администратором
                continue;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                try
                {
                    await Task.Delay(250, lifetimeToken);
                }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private void AppendBurstChunk(byte[] chunk)
    {
        _burstChunks.AddLast(chunk);
        _burstBytesTotal += chunk.Length;

        while (_burstBytesTotal > MaxBurstBytes && _burstChunks.Count > 1)
        {
            var first = _burstChunks.First!.Value;
            _burstChunks.RemoveFirst();
            _burstBytesTotal -= first.Length;
        }
    }

    private void PopulateBurstFromTrack(FileStream fs, Mp3TrackInfo trackInfo, long currentStreamedBytes)
    {
        long currentFilePos = fs.Position;
        try
        {
            long burstStart = Math.Max(trackInfo.AudioOffset, trackInfo.AudioOffset + currentStreamedBytes - MaxBurstBytes);
            if (TryFindNextFrame(fs, burstStart, currentFilePos, out long alignedBurstStart, out _))
            {
                burstStart = alignedBurstStart;
            }

            int bytesToRead = (int)Math.Max(0, currentFilePos - burstStart);
            if (bytesToRead > 0)
            {
                byte[] tempBuf = new byte[bytesToRead];
                fs.Seek(burstStart, SeekOrigin.Begin);
                int read = fs.Read(tempBuf, 0, bytesToRead);
                if (read > 0)
                {
                    byte[] slice = new byte[read];
                    Buffer.BlockCopy(tempBuf, 0, slice, 0, read);
                    AppendBurstChunk(slice);
                }
            }

            if (_burstBytesTotal < 32 * 1024)
            {
                AppendBurstChunk((byte[])CachedSilenceBurstBuffer.Clone());
            }
        }
        catch
        {
            AppendBurstChunk((byte[])CachedSilenceBurstBuffer.Clone());
        }
        finally
        {
            try { fs.Seek(currentFilePos, SeekOrigin.Begin); } catch { }
        }
    }

    private byte[] CombineBurstChunks()
    {
        if (_burstChunks.Count == 0 || _burstBytesTotal <= 0)
            return (byte[])CachedSilenceBurstBuffer.Clone();

        byte[] result = new byte[_burstBytesTotal];
        int offset = 0;
        foreach (var c in _burstChunks)
        {
            Buffer.BlockCopy(c, 0, result, offset, c.Length);
            offset += c.Length;
        }

        // Выравниваем срез по первому валидному MPEG-фрейму
        int startFrame = FindFirstValidFrameIndex(result);
        if (startFrame > 0 && startFrame < result.Length - 1024)
        {
            int validLength = result.Length - startFrame;
            byte[] aligned = new byte[validLength];
            Buffer.BlockCopy(result, startFrame, aligned, 0, validLength);
            return aligned;
        }

        return result;
    }

    private static int FindFirstValidFrameIndex(ReadOnlySpan<byte> data)
    {
        int limit = Math.Min(data.Length - 4, 4096);
        for (int i = 0; i <= limit; i++)
        {
            if (data[i] == 0xFF && (data[i + 1] & 0xE0) == 0xE0)
            {
                if (TryParseMpegHeader(data.Slice(i, 4), out var header))
                {
                    int nextOffset = i + header.FrameLength;
                    if (nextOffset + 4 <= data.Length)
                    {
                        if (data[nextOffset] == 0xFF && (data[nextOffset + 1] & 0xE0) == 0xE0 &&
                            TryParseMpegHeader(data.Slice(nextOffset, 4), out var nextHdr) &&
                            nextHdr.MpegVersion == header.MpegVersion &&
                            nextHdr.Layer == header.Layer &&
                            nextHdr.SampleRate == header.SampleRate)
                        {
                            return i;
                        }
                    }
                    else
                    {
                        return i;
                    }
                }
            }
        }
        return 0;
    }

    /// <summary>
    /// Совместимость: возвращает определенный битрейт в bps.
    /// </summary>
    public static int DetectMp3Bitrate(string filePath) => ParseMp3TrackInfo(filePath).Bitrate;

    /// <summary>
    /// Парсит метаданные MP3 файла: пропускает ID3v2, извлекает TLEN, определяет VBR (Xing/VBRI) или CBR.
    /// </summary>
    public static Mp3TrackInfo ParseMp3TrackInfo(string filePath)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long fileLength = fs.Length;
            if (fileLength == 0)
            {
                return new Mp3TrackInfo();
            }

            // 1. Проверяем наличие ID3v2 тега и извлекаем размер и TLEN (при наличии)
            long id3v2Size = 0;
            long? tlenMs = null;

            byte[] header = new byte[10];
            int headerRead = fs.Read(header, 0, 10);
            if (headerRead == 10 && header[0] == 0x49 && header[1] == 0x44 && header[2] == 0x33) // "ID3"
            {
                byte majorVersion = header[3];
                byte flags = header[5];
                int tagDataSize = ((header[6] & 0x7F) << 21) |
                                  ((header[7] & 0x7F) << 14) |
                                  ((header[8] & 0x7F) << 7) |
                                   (header[9] & 0x7F);

                bool hasFooter = (majorVersion == 4) && ((flags & 0x10) != 0);
                id3v2Size = 10L + tagDataSize + (hasFooter ? 10 : 0);
                if (id3v2Size > fileLength)
                {
                    id3v2Size = fileLength;
                }

                tlenMs = TryExtractTlen(fs, majorVersion, flags, tagDataSize);
            }

            // 2. Проверяем наличие ID3v1 в конце файла (128 байт, начинаются с "TAG")
            long id3v1Size = 0;
            if (fileLength >= 128)
            {
                fs.Seek(fileLength - 128, SeekOrigin.Begin);
                byte[] id3v1Buf = new byte[3];
                if (fs.Read(id3v1Buf, 0, 3) == 3 &&
                    id3v1Buf[0] == 0x54 && id3v1Buf[1] == 0x41 && id3v1Buf[2] == 0x47) // "TAG"
                {
                    id3v1Size = 128;
                }
            }

            // 3. Поиск первого валидного MPEG аудиофрейма после ID3v2 тега
            long audioStartOffset = id3v2Size;
            fs.Seek(audioStartOffset, SeekOrigin.Begin);

            if (TryFindNextFrame(fs, audioStartOffset, fileLength, out long frameOffset, out MpegFrameHeader frameHeader))
            {
                long audioBytes = Math.Max(0, fileLength - frameOffset - id3v1Size);
                long durationMs;

                // 4. Проверяем заголовок Xing / Info / VBRI внутри найденного аудиофрейма
                fs.Seek(frameOffset, SeekOrigin.Begin);
                byte[] frameBuf = new byte[Math.Min(frameHeader.FrameLength, 512)];
                int frameBytesRead = fs.Read(frameBuf, 0, frameBuf.Length);

                if (TryParseVbrHeader(frameBuf, frameBytesRead, frameHeader, out int totalFrames, out long totalVbrBytes))
                {
                    long effectiveAudioBytes = totalVbrBytes > 0 ? totalVbrBytes : audioBytes;
                    durationMs = (long)((double)totalFrames * frameHeader.SamplesPerFrame * 1000.0 / frameHeader.SampleRate);
                    int avgBitrate = durationMs > 0
                        ? (int)((effectiveAudioBytes * 8.0) / (durationMs / 1000.0))
                        : frameHeader.Bitrate;

                    return new Mp3TrackInfo
                    {
                        Bitrate = avgBitrate,
                        DurationMs = durationMs,
                        AudioOffset = frameOffset,
                        AudioLength = effectiveAudioBytes,
                        SampleRate = frameHeader.SampleRate,
                        Channels = frameHeader.Channels,
                        HasVbrHeader = true
                    };
                }

                // 5. Если VBR заголовка нет, но есть TLEN из ID3v2
                if (tlenMs.HasValue && tlenMs.Value > 0)
                {
                    durationMs = tlenMs.Value;
                    int calcBitrate = (int)((audioBytes * 8.0) / (durationMs / 1000.0));
                    int bitrate = (calcBitrate >= 8000 && calcBitrate <= 640000) ? calcBitrate : frameHeader.Bitrate;

                    return new Mp3TrackInfo
                    {
                        Bitrate = bitrate,
                        DurationMs = durationMs,
                        AudioOffset = frameOffset,
                        AudioLength = audioBytes,
                        SampleRate = frameHeader.SampleRate,
                        Channels = frameHeader.Channels,
                        HasVbrHeader = false
                    };
                }

                // 6. CBR или VBR без заголовков: сканируем фреймы для точного определения среднего битрейта (защита от ~2x длины трека)
                var (effectiveBitrate, isVbr) = ScanFramesBitrate(fs, frameOffset, frameHeader, fileLength, id3v1Size);
                durationMs = effectiveBitrate > 0
                    ? (long)((double)audioBytes * 8.0 * 1000.0 / effectiveBitrate)
                    : (frameHeader.Bitrate > 0 ? (long)((double)audioBytes * 8.0 * 1000.0 / frameHeader.Bitrate) : 0);

                return new Mp3TrackInfo
                {
                    Bitrate = effectiveBitrate > 0 ? effectiveBitrate : frameHeader.Bitrate,
                    DurationMs = durationMs,
                    AudioOffset = frameOffset,
                    AudioLength = audioBytes,
                    SampleRate = frameHeader.SampleRate,
                    Channels = frameHeader.Channels,
                    HasVbrHeader = isVbr
                };
            }

            // Если аудиофрейм не найден (например, OGG или поврежденный файл)
            long fallbackAudioBytes = Math.Max(0, fileLength - id3v2Size - id3v1Size);
            long fallbackDurationMs = (long)((double)fallbackAudioBytes * 8.0 * 1000.0 / 128000);
            return new Mp3TrackInfo
            {
                Bitrate = 128000,
                DurationMs = fallbackDurationMs,
                AudioOffset = id3v2Size,
                AudioLength = fallbackAudioBytes,
                SampleRate = 44100,
                Channels = 2,
                HasVbrHeader = false
            };
        }
        catch
        {
            return new Mp3TrackInfo();
        }
    }

    private static long? TryExtractTlen(FileStream fs, byte majorVersion, byte flags, int tagDataSize)
    {
        try
        {
            long tagStart = 10;
            long tagEnd = Math.Min(fs.Length, 10L + tagDataSize);
            fs.Seek(tagStart, SeekOrigin.Begin);

            // Пропускаем Extended Header, если флаг установлен
            if ((flags & 0x40) != 0 && tagEnd - fs.Position >= 4)
            {
                byte[] extHdrBuf = new byte[4];
                fs.ReadExactly(extHdrBuf, 0, 4);
                int extSize;
                if (majorVersion == 4)
                {
                    extSize = ((extHdrBuf[0] & 0x7F) << 21) | ((extHdrBuf[1] & 0x7F) << 14) |
                              ((extHdrBuf[2] & 0x7F) << 7) | (extHdrBuf[3] & 0x7F);
                    extSize = Math.Max(0, extSize - 4);
                }
                else
                {
                    extSize = (extHdrBuf[0] << 24) | (extHdrBuf[1] << 16) | (extHdrBuf[2] << 8) | extHdrBuf[3];
                }
                if (extSize > 0 && fs.Position + extSize < tagEnd)
                {
                    fs.Seek(extSize, SeekOrigin.Current);
                }
            }

            if (majorVersion == 2) // ID3v2.2 (3-char frame ID, 3-byte size)
            {
                byte[] fHdr = new byte[6];
                while (fs.Position + 6 <= tagEnd)
                {
                    if (fs.Read(fHdr, 0, 6) < 6) break;
                    if (fHdr[0] == 0) break; // Padding
                    string id = Encoding.ASCII.GetString(fHdr, 0, 3);
                    int size = (fHdr[3] << 16) | (fHdr[4] << 8) | fHdr[5];
                    if (size <= 0 || fs.Position + size > tagEnd) break;

                    if (id == "TLE")
                    {
                        byte[] data = new byte[Math.Min(size, 256)];
                        fs.ReadExactly(data, 0, data.Length);
                        return ParseTlenText(data);
                    }
                    fs.Seek(size, SeekOrigin.Current);
                }
            }
            else // ID3v2.3 / ID3v2.4 (4-char frame ID, 4-byte size, 2-byte flags)
            {
                byte[] fHdr = new byte[10];
                while (fs.Position + 10 <= tagEnd)
                {
                    if (fs.Read(fHdr, 0, 10) < 10) break;
                    if (fHdr[0] == 0) break; // Padding
                    string id = Encoding.ASCII.GetString(fHdr, 0, 4);
                    int size;
                    if (majorVersion == 4)
                    {
                        size = ((fHdr[4] & 0x7F) << 21) | ((fHdr[5] & 0x7F) << 14) |
                               ((fHdr[6] & 0x7F) << 7) | (fHdr[7] & 0x7F);
                    }
                    else
                    {
                        size = (fHdr[4] << 24) | (fHdr[5] << 16) | (fHdr[6] << 8) | fHdr[7];
                    }

                    if (size <= 0 || fs.Position + size > tagEnd) break;

                    if (id == "TLEN")
                    {
                        byte[] data = new byte[Math.Min(size, 256)];
                        fs.ReadExactly(data, 0, data.Length);
                        return ParseTlenText(data);
                    }
                    fs.Seek(size, SeekOrigin.Current);
                }
            }
        }
        catch { }
        return null;
    }

    private static long? ParseTlenText(byte[] data)
    {
        if (data.Length < 2) return null;
        byte encoding = data[0];
        string text = encoding switch
        {
            0 => Encoding.Latin1.GetString(data, 1, data.Length - 1),
            1 => Encoding.Unicode.GetString(data, 1, data.Length - 1),
            2 => Encoding.BigEndianUnicode.GetString(data, 1, data.Length - 1),
            3 => Encoding.UTF8.GetString(data, 1, data.Length - 1),
            _ => Encoding.Latin1.GetString(data, 1, data.Length - 1)
        };

        text = text.Trim('\0', ' ', '\r', '\n', '\t');
        if (long.TryParse(text, out long ms) && ms > 0)
        {
            return ms;
        }
        return null;
    }

    private static (int AverageBitrate, bool IsVbr) ScanFramesBitrate(
        FileStream fs,
        long startOffset,
        MpegFrameHeader firstHeader,
        long fileLength,
        long id3v1Size)
    {
        long sumBitrate = firstHeader.Bitrate;
        int count = 1;
        bool isVbr = false;

        long savedPos = fs.Position;
        try
        {
            long currentOffset = startOffset + firstHeader.FrameLength;
            long endOffset = fileLength - id3v1Size;
            long audioBytes = endOffset - startOffset;
            byte[] hdrBuf = new byte[4];

            if (audioBytes > 100 * 1024)
            {
                // Для файлов > 100 КБ равномерно распределяем 10 выборок по всему треку (5%..95%),
                // чтобы тихое интро с низким битрейтом не искажало средний битрейт и не удваивало длительность трека (~x2 bug).
                sumBitrate = 0;
                count = 0;
                double[] sampleFractions = { 0.05, 0.15, 0.25, 0.35, 0.45, 0.55, 0.65, 0.75, 0.85, 0.95 };
                foreach (var frac in sampleFractions)
                {
                    long targetPos = startOffset + (long)(audioBytes * frac);
                    long scanLimit = Math.Min(endOffset, targetPos + 65536);
                    if (TryFindNextFrame(fs, targetPos, scanLimit, out long framePos, out var sampleHdr))
                    {
                        long fPos = framePos;
                        for (int k = 0; k < 5 && fPos + 4 <= endOffset; k++)
                        {
                            fs.Seek(fPos, SeekOrigin.Begin);
                            if (fs.Read(hdrBuf, 0, 4) < 4) break;
                            if (TryParseMpegHeader(hdrBuf, out var h) &&
                                h.MpegVersion == firstHeader.MpegVersion &&
                                h.Layer == firstHeader.Layer &&
                                h.SampleRate == firstHeader.SampleRate &&
                                h.Bitrate > 0 && h.FrameLength > 4)
                            {
                                sumBitrate += h.Bitrate;
                                count++;
                                if (h.Bitrate != firstHeader.Bitrate) isVbr = true;
                                fPos += h.FrameLength;
                            }
                            else break;
                        }
                    }
                }

                if (count == 0)
                {
                    sumBitrate = firstHeader.Bitrate;
                    count = 1;
                }
            }
            else
            {
                // Для коротких файлов сканируем последовательные фреймы от начала
                int consecutiveFrames = 0;
                while (currentOffset + 4 <= endOffset && consecutiveFrames < 80)
                {
                    fs.Seek(currentOffset, SeekOrigin.Begin);
                    if (fs.Read(hdrBuf, 0, 4) < 4) break;

                    if (TryParseMpegHeader(hdrBuf, out var hdr) &&
                        hdr.MpegVersion == firstHeader.MpegVersion &&
                        hdr.Layer == firstHeader.Layer &&
                        hdr.SampleRate == firstHeader.SampleRate &&
                        hdr.Bitrate > 0 && hdr.FrameLength > 4)
                    {
                        sumBitrate += hdr.Bitrate;
                        count++;
                        if (hdr.Bitrate != firstHeader.Bitrate)
                        {
                            isVbr = true;
                        }
                        currentOffset += hdr.FrameLength;
                        consecutiveFrames++;
                    }
                    else
                    {
                        break;
                    }
                }
            }
        }
        catch
        {
            // В случае непредвиденных ошибок используем битрейт первого фрейма
        }
        finally
        {
            try { fs.Position = savedPos; } catch { }
        }

        int avgBitrate = count > 0 ? (int)(sumBitrate / count) : firstHeader.Bitrate;
        return (avgBitrate, isVbr);
    }

    private static bool TryFindNextFrame(FileStream fs, long startOffset, long fileLength, out long frameOffset, out MpegFrameHeader frameHeader)
    {
        frameOffset = 0;
        frameHeader = default;

        const int scanBufferSize = 65536;
        byte[] scanBuf = new byte[scanBufferSize];
        long maxScan = Math.Min(fileLength, startOffset + 2 * 1024 * 1024); // сканируем до 2 МБ после ID3
        long currentPos = startOffset;

        while (currentPos < maxScan)
        {
            fs.Seek(currentPos, SeekOrigin.Begin);
            int read = fs.Read(scanBuf, 0, (int)Math.Min(scanBuf.Length, maxScan - currentPos));
            if (read < 4) break;

            for (int i = 0; i <= read - 4; i++)
            {
                if (scanBuf[i] == 0xFF && (scanBuf[i + 1] & 0xE0) == 0xE0)
                {
                    if (TryParseMpegHeader(scanBuf.AsSpan(i, 4), out var candidate))
                    {
                        long candOffset = currentPos + i;

                        if (ValidateFrame(fs, candOffset, candidate, fileLength))
                        {
                            frameOffset = candOffset;
                            frameHeader = candidate;
                            return true;
                        }
                    }
                }
            }

            currentPos += (read - 3);
        }

        return false;
    }

    private static bool ValidateFrame(FileStream fs, long frameOffset, MpegFrameHeader header, long fileLength)
    {
        if (frameOffset + header.FrameLength >= fileLength)
        {
            return true;
        }

        long savedPos = fs.Position;
        try
        {
            fs.Seek(frameOffset, SeekOrigin.Begin);
            byte[] frameBuf = new byte[Math.Min(header.FrameLength, 256)];
            int read = fs.Read(frameBuf, 0, frameBuf.Length);
            if (TryParseVbrHeader(frameBuf, read, header, out _, out _))
            {
                return true;
            }

            fs.Seek(frameOffset + header.FrameLength, SeekOrigin.Begin);
            byte[] nextHdr = new byte[4];
            if (fs.Read(nextHdr, 0, 4) == 4)
            {
                if (TryParseMpegHeader(nextHdr, out var nextFrameHeader))
                {
                    if (nextFrameHeader.MpegVersion == header.MpegVersion &&
                        nextFrameHeader.Layer == header.Layer &&
                        nextFrameHeader.SampleRate == header.SampleRate)
                    {
                        return true;
                    }
                }
            }
        }
        catch { }
        finally
        {
            fs.Position = savedPos;
        }

        return false;
    }

    private static bool TryParseMpegHeader(ReadOnlySpan<byte> bytes, out MpegFrameHeader header)
    {
        header = default;
        if (bytes.Length < 4) return false;

        byte b0 = bytes[0];
        byte b1 = bytes[1];
        byte b2 = bytes[2];
        byte b3 = bytes[3];

        if (b0 != 0xFF || (b1 & 0xE0) != 0xE0)
            return false;

        int mpegVerRaw = (b1 >> 3) & 0x03;
        if (mpegVerRaw == 1) return false; // 01 = reserved

        int layerRaw = (b1 >> 1) & 0x03;
        if (layerRaw == 0) return false; // 00 = reserved

        bool hasCrc = (b1 & 0x01) == 0;

        int bitIdx = (b2 >> 4) & 0x0F;
        if (bitIdx == 0 || bitIdx == 15) return false;

        int rateIdx = (b2 >> 2) & 0x03;
        if (rateIdx == 3) return false;

        int padding = (b2 >> 1) & 0x01;
        int channelMode = (b3 >> 6) & 0x03;
        int emphasis = b3 & 0x03;
        if (emphasis == 2) return false;

        int mpegVer = mpegVerRaw switch
        {
            3 => 1,
            2 => 2,
            _ => 3 // MPEG 2.5
        };

        int layer = layerRaw switch
        {
            3 => 1, // Layer I
            2 => 2, // Layer II
            _ => 3  // Layer III
        };

        int bitrateKbps;
        if (mpegVer == 1)
        {
            if (layer == 1)
            {
                int[] bitrates = { 0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448 };
                bitrateKbps = bitrates[bitIdx];
            }
            else if (layer == 2)
            {
                int[] bitrates = { 0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384 };
                bitrateKbps = bitrates[bitIdx];
            }
            else
            {
                int[] bitrates = { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320 };
                bitrateKbps = bitrates[bitIdx];
            }
        }
        else
        {
            if (layer == 1)
            {
                int[] bitrates = { 0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256 };
                bitrateKbps = bitrates[bitIdx];
            }
            else
            {
                int[] bitrates = { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160 };
                bitrateKbps = bitrates[bitIdx];
            }
        }

        int sampleRate;
        if (mpegVer == 1)
        {
            int[] rates = { 44100, 48000, 32000 };
            sampleRate = rates[rateIdx];
        }
        else if (mpegVer == 2)
        {
            int[] rates = { 22050, 24000, 16000 };
            sampleRate = rates[rateIdx];
        }
        else
        {
            int[] rates = { 11025, 12000, 8000 };
            sampleRate = rates[rateIdx];
        }

        int samplesPerFrame;
        if (layer == 1)
        {
            samplesPerFrame = 384;
        }
        else if (layer == 2)
        {
            samplesPerFrame = 1152;
        }
        else
        {
            samplesPerFrame = (mpegVer == 1) ? 1152 : 576;
        }

        int bitrateBps = bitrateKbps * 1000;
        int frameLength;
        if (layer == 1)
        {
            frameLength = ((12 * bitrateBps / sampleRate) + padding) * 4;
        }
        else
        {
            frameLength = (samplesPerFrame / 8 * bitrateBps) / sampleRate + padding;
        }

        if (frameLength < 4) return false;

        header = new MpegFrameHeader
        {
            MpegVersion = mpegVer,
            Layer = layer,
            HasCrc = hasCrc,
            Bitrate = bitrateBps,
            SampleRate = sampleRate,
            Channels = (channelMode == 3) ? 1 : 2,
            ChannelMode = channelMode,
            SamplesPerFrame = samplesPerFrame,
            FrameLength = frameLength
        };

        return true;
    }

    private static bool TryParseVbrHeader(byte[] frameBuf, int read, MpegFrameHeader frameHeader, out int totalFrames, out long totalAudioBytes)
    {
        totalFrames = 0;
        totalAudioBytes = 0;

        if (read < 36) return false;

        int sideInfoLen = (frameHeader.MpegVersion == 1)
            ? (frameHeader.ChannelMode == 3 ? 17 : 32)
            : (frameHeader.ChannelMode == 3 ? 9 : 17);

        int expectedXingOffset = 4 + (frameHeader.HasCrc ? 2 : 0) + sideInfoLen;

        int xingPos = -1;
        if (expectedXingOffset + 8 <= read && IsXingOrInfo(frameBuf, expectedXingOffset))
        {
            xingPos = expectedXingOffset;
        }
        else
        {
            for (int i = 4; i <= read - 16; i++)
            {
                if (IsXingOrInfo(frameBuf, i))
                {
                    xingPos = i;
                    break;
                }
                if (IsVbri(frameBuf, i))
                {
                    return ParseVbriHeader(frameBuf, i, read, out totalFrames, out totalAudioBytes);
                }
            }
        }

        if (xingPos >= 0 && xingPos + 8 <= read)
        {
            int flags = (frameBuf[xingPos + 4] << 24) | (frameBuf[xingPos + 5] << 16) |
                        (frameBuf[xingPos + 6] << 8) | frameBuf[xingPos + 7];
            int pos = xingPos + 8;

            if ((flags & 0x01) != 0 && pos + 4 <= read)
            {
                totalFrames = (frameBuf[pos] << 24) | (frameBuf[pos + 1] << 16) |
                              (frameBuf[pos + 2] << 8) | frameBuf[pos + 3];
                pos += 4;
            }

            if ((flags & 0x02) != 0 && pos + 4 <= read)
            {
                totalAudioBytes = (uint)((frameBuf[pos] << 24) | (frameBuf[pos + 1] << 16) |
                                         (frameBuf[pos + 2] << 8) | frameBuf[pos + 3]);
                pos += 4;
            }

            if (totalFrames > 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsXingOrInfo(byte[] buf, int offset)
    {
        return (buf[offset] == 0x58 && buf[offset + 1] == 0x69 && buf[offset + 2] == 0x6E && buf[offset + 3] == 0x67) || // "Xing"
               (buf[offset] == 0x49 && buf[offset + 1] == 0x6E && buf[offset + 2] == 0x66 && buf[offset + 3] == 0x6F);   // "Info"
    }

    private static bool IsVbri(byte[] buf, int offset)
    {
        return buf[offset] == 0x56 && buf[offset + 1] == 0x42 && buf[offset + 2] == 0x52 && buf[offset + 3] == 0x49;   // "VBRI"
    }

    private static bool ParseVbriHeader(byte[] buf, int offset, int read, out int totalFrames, out long totalAudioBytes)
    {
        totalFrames = 0;
        totalAudioBytes = 0;

        if (offset + 18 <= read)
        {
            totalAudioBytes = (uint)((buf[offset + 10] << 24) | (buf[offset + 11] << 16) |
                                     (buf[offset + 12] << 8) | buf[offset + 13]);
            totalFrames = (buf[offset + 14] << 24) | (buf[offset + 15] << 16) |
                          (buf[offset + 16] << 8) | buf[offset + 17];
            return totalFrames > 0;
        }
        return false;
    }

    private readonly struct MpegFrameHeader
    {
        public int MpegVersion { get; init; }
        public int Layer { get; init; }
        public bool HasCrc { get; init; }
        public int Bitrate { get; init; }
        public int SampleRate { get; init; }
        public int Channels { get; init; }
        public int ChannelMode { get; init; }
        public int SamplesPerFrame { get; init; }
        public int FrameLength { get; init; }
    }

    public void Dispose()
    {
        try
        {
            _stationLifetimeCts.Cancel();
            _stationLifetimeCts.Dispose();
        }
        catch { }

        try
        {
            _liveDjWakeupCts.Cancel();
            _liveDjWakeupCts.Dispose();
        }
        catch { }

        foreach (var sub in _subscribers.Values)
        {
            sub.Writer.TryComplete();
        }
        _subscribers.Clear();
    }
}

/// <summary>
/// Метаданные аудиофайла, определенные парсером фреймов MP3.
/// </summary>
public sealed class Mp3TrackInfo
{
    public int Bitrate { get; init; } = 128000;
    public long DurationMs { get; init; }
    public long AudioOffset { get; init; }
    public long AudioLength { get; init; }
    public int SampleRate { get; init; } = 44100;
    public int Channels { get; init; } = 2;
    public bool HasVbrHeader { get; init; }
}

