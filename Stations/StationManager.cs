using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace RadioServer.Stations;

public record ActivityLogEntry(
    DateTime Timestamp,
    string Ip,
    string Station,
    string Action
);

public record StationDetailDto(
    string Id,
    string Name,
    int Listeners,
    string CurrentTrack,
    int TrackCount,
    IReadOnlyList<string> Tracks,
    int TrackDurationSeconds = 0,
    int TrackRemainingSeconds = 0,
    bool IsPaused = false,
    bool IsLiveDj = false,
    int? MaxListeners = null
);

public record AdminStatsDto(
    TimeSpan Uptime,
    double MemoryMb,
    long TotalBytesSent,
    int TotalListeners,
    int StationsCount,
    IEnumerable<StationDetailDto> Stations,
    IEnumerable<ActivityLogEntry> RecentLogs
);

/// <summary>
/// Синглтон-сервис для управления всеми радиостанциями сервера.
/// Потокобезопасен и обеспечивает централизованный доступ к станциям.
/// </summary>
public class StationManager
{
    private readonly ConcurrentDictionary<string, RadioStation> _stations = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<ActivityLogEntry> _activityLogs = new();
    private readonly DateTime _startTime = DateTime.UtcNow;
    private long _totalBytesSent = 0;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<StationManager> _logger;

    public StationManager(IWebHostEnvironment env, ILogger<StationManager> logger)
    {
        _env = env;
        _logger = logger;
        InitializeStations();
    }

    public string AudioRootPath => Path.Combine(_env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot"), "audio");

    private void InitializeStations()
    {
        var audioRoot = AudioRootPath;
        if (!Directory.Exists(audioRoot))
        {
            Directory.CreateDirectory(audioRoot);
        }

        var predefined = new (string Id, string Name)[]
        {
            ("rock", "Рок-Волна"),
            ("jazz", "Джаз-Кафе"),
            ("pop", "Поп-Хит"),
            ("synthwave", "Синтвейв")
        };

        foreach (var (id, name) in predefined)
        {
            var stationDir = Path.Combine(audioRoot, id);
            Directory.CreateDirectory(stationDir);
            EnsureSampleTracks(stationDir, id, name);

            var station = new RadioStation(id, name, stationDir);
            _stations[id] = station;
            _logger.LogInformation("Станция зарегистрирована: {Id} ({Name}) - папка: {Path}", id, name, stationDir);
        }

        // Автоматическое обнаружение других станций в директории audio
        try
        {
            var directories = Directory.GetDirectories(audioRoot);
            foreach (var dir in directories)
            {
                var dirId = Path.GetFileName(dir).ToLowerInvariant();
                if (!_stations.ContainsKey(dirId))
                {
                    var dirName = char.ToUpper(dirId[0]) + dirId[1..];
                    EnsureSampleTracks(dir, dirId, dirName);
                    var station = new RadioStation(dirId, dirName, dir);
                    _stations[dirId] = station;
                    _logger.LogInformation("Обнаружена станция из папки: {Id} ({Name})", dirId, dirName);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ошибка при автообнаружении станций в {Path}", audioRoot);
        }
    }

    /// <summary>
    /// Добавление новой станции.
    /// </summary>
    public (bool Success, string Message) AddStation(string id, string name)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            return (false, "ID и название станции не могут быть пустыми.");

        id = id.Trim().ToLowerInvariant();
        name = name.Trim();

        if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-z0-9_\\-]+$"))
            return (false, "ID станции может содержать только латинские буквы, цифры, дефис и знак подчеркивания.");

        if (_stations.ContainsKey(id))
            return (false, $"Станция с ID '{id}' уже существует.");

        var stationDir = Path.Combine(AudioRootPath, id);
        Directory.CreateDirectory(stationDir);
        EnsureSampleTracks(stationDir, id, name);

        var station = new RadioStation(id, name, stationDir);
        _stations[id] = station;
        RecordActivity("server", name, "Станция создана");

        return (true, "Станция успешно добавлена.");
    }

    /// <summary>
    /// Получение станции по её идентификатору.
    /// </summary>
    public RadioStation? GetStation(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        _stations.TryGetValue(id, out var station);
        return station;
    }

    /// <summary>
    /// Удаление и освобождение ресурсов станции.
    /// </summary>
    public bool RemoveStation(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return false;

        id = id.Trim().ToLowerInvariant();
        if (_stations.TryRemove(id, out var station))
        {
            station.Dispose();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Получение всех зарегистрированных станций.
    /// </summary>
    public IEnumerable<RadioStation> GetAllStations() => _stations.Values;

    /// <summary>
    /// Получение информации обо всех станциях для JSON-эндпоинта /stations.
    /// </summary>
    public IEnumerable<StationInfo> GetStationsInfo()
    {
        return _stations.Values.Select(s => s.ToInfo());
    }

    /// <summary>
    /// Учет переданных байт в эфир.
    /// </summary>
    public void AddStreamedBytes(long count)
    {
        Interlocked.Add(ref _totalBytesSent, count);
    }

    /// <summary>
    /// Логирование активности слушателей для админ-панели.
    /// </summary>
    public void RecordActivity(string ip, string station, string action)
    {
        _activityLogs.Enqueue(new ActivityLogEntry(DateTime.UtcNow, ip, station, action));
        while (_activityLogs.Count > 100 && _activityLogs.TryDequeue(out _)) { }
    }

    /// <summary>
    /// Сбор сводной статистики для серверной панели управления.
    /// </summary>
    public AdminStatsDto GetAdminStats()
    {
        var stations = _stations.Values.Select(s => new StationDetailDto(
            s.Id,
            s.Name,
            s.ListenersCount,
            s.GetCurrentTrackName(),
            s.TrackCount,
            s.GetTracks(),
            s.TrackDurationSeconds,
            s.TrackRemainingSeconds,
            s.IsPaused,
            s.IsLiveDj,
            s.MaxListeners
        )).ToList();

        var memMb = Math.Round((double)Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024), 2);

        return new AdminStatsDto(
            DateTime.UtcNow - _startTime,
            memMb,
            Interlocked.Read(ref _totalBytesSent),
            stations.Sum(s => s.Listeners),
            stations.Count,
            stations,
            _activityLogs.Reverse().Take(30).ToList()
        );
    }

    /// <summary>
    /// Создает демонстрационные валидные MP3 файлы с ID3-тегами, если директория пуста.
    /// </summary>
    private void EnsureSampleTracks(string stationDir, string stationId, string stationName)
    {
        var existing = Directory.GetFiles(stationDir, "*.mp3")
            .Concat(Directory.GetFiles(stationDir, "*.ogg"))
            .ToArray();

        if (existing.Length > 0)
            return;

        try
        {
            for (int i = 1; i <= 3; i++)
            {
                var trackFileName = $"track{i}.mp3";
                var trackFilePath = Path.Combine(stationDir, trackFileName);
                var trackTitle = $"{stationName} - Трек #{i}";

                byte[] mp3Data = GenerateSampleMp3(trackTitle, durationSeconds: 8);
                File.WriteAllBytes(trackFilePath, mp3Data);
                _logger.LogInformation("Сгенерирован тестовый трек: {Path}", trackFilePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось создать демонстрационные файлы в {Dir}", stationDir);
        }
    }

    /// <summary>
    /// Генерирует валидный MP3 файл (MPEG-1 Layer 3, 128 kbps, 44.1 kHz Joint Stereo) с тегом ID3v2.
    /// </summary>
    private static byte[] GenerateSampleMp3(string title, int durationSeconds)
    {
        using var ms = new MemoryStream();

        byte[] titleBytes = Encoding.UTF8.GetBytes(title);
        byte[] tit2FrameData = new byte[1 + titleBytes.Length];
        tit2FrameData[0] = 0x03;
        Buffer.BlockCopy(titleBytes, 0, tit2FrameData, 1, titleBytes.Length);

        using (var tagStream = new MemoryStream())
        {
            tagStream.Write(Encoding.ASCII.GetBytes("TIT2"));
            tagStream.Write(new byte[] { 0x00, 0x00, 0x00, (byte)tit2FrameData.Length });
            tagStream.Write(new byte[] { 0x00, 0x00 });
            tagStream.Write(tit2FrameData);

            byte[] tagBody = tagStream.ToArray();
            int tagSize = tagBody.Length;

            ms.Write(Encoding.ASCII.GetBytes("ID3"));
            ms.WriteByte(0x03);
            ms.WriteByte(0x00);
            ms.WriteByte(0x00);
            ms.WriteByte((byte)((tagSize >> 21) & 0x7F));
            ms.WriteByte((byte)((tagSize >> 14) & 0x7F));
            ms.WriteByte((byte)((tagSize >> 7) & 0x7F));
            ms.WriteByte((byte)(tagSize & 0x7F));
            ms.Write(tagBody);
        }

        const int frameSize = 417;
        const int framesPerSecond = 38;
        int totalFrames = durationSeconds * framesPerSecond;

        byte[] frame = new byte[frameSize];
        frame[0] = 0xFF;
        frame[1] = 0xFB;
        frame[2] = 0x90;
        frame[3] = 0x64;

        for (int i = 0; i < totalFrames; i++)
        {
            ms.Write(frame);
        }

        return ms.ToArray();
    }
}
