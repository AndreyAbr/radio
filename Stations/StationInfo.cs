namespace RadioServer.Stations;

/// <summary>
/// Информация о радиостанции для передачи клиенту через API (/stations).
/// Включает актуальные данные о времени текущего трека (длительность и сколько осталось).
/// </summary>
public record StationInfo(
    string Id,
    string Name,
    int Listeners,
    string CurrentTrack,
    int TrackDurationSeconds = 0,
    int TrackRemainingSeconds = 0,
    bool IsPaused = false,
    bool IsLiveDj = false
);
