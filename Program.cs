using RadioServer.Stations;

var builder = WebApplication.CreateBuilder(args);

// Регистрация менеджера радиостанций как Singleton
builder.Services.AddSingleton<StationManager>();

var app = builder.Build();

// Инициализация сервиса станций при старте
_ = app.Services.GetRequiredService<StationManager>();

// Поддержка WebSockets для прямого включения микрофона и студийного микшера
app.UseWebSockets();

// Раздача статических файлов (index.html, admin.html, style.css, audio, etc.)
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        ctx.Context.Response.Headers.Append("Access-Control-Allow-Origin", "*");
        ctx.Context.Response.Headers.Append("Access-Control-Allow-Headers", "*");
        ctx.Context.Response.Headers.Append("Access-Control-Allow-Methods", "GET, HEAD, OPTIONS");
    }
});

// Перенаправление /admin на /admin.html
app.MapGet("/admin", () => Results.Redirect("/admin.html"));

// Эндпоинт 1: Получение списка станций для плеера
app.MapGet("/stations", (StationManager manager) =>
{
    return Results.Json(manager.GetStationsInfo());
});

// Эндпоинты Админ-панели (Server Dashboard)
app.MapGet("/api/admin/stats", (StationManager manager) =>
{
    return Results.Json(manager.GetAdminStats());
});

app.MapPost("/api/admin/stations/{id}/skip", (string id, StationManager manager) =>
{
    var station = manager.GetStation(id);
    if (station == null) return Results.NotFound(new { message = "Станция не найдена." });

    var newTrack = station.SkipTrack();
    manager.RecordActivity("admin", station.Name, $"Принудительная смена трека на: {newTrack}");
    return Results.Ok(new { message = $"Трек переключен на: {newTrack}", currentTrack = newTrack });
});

// Пауза эфира
app.MapPost("/api/admin/stations/{id}/pause", (string id, StationManager manager) =>
{
    var station = manager.GetStation(id);
    if (station == null) return Results.NotFound(new { message = "Станция не найдена." });

    station.Pause();
    manager.RecordActivity("admin", station.Name, "⏸ Эфир поставлен на паузу");
    return Results.Ok(new { isPaused = true, message = "Эфир поставлен на паузу" });
});

// Возобновление эфира
app.MapPost("/api/admin/stations/{id}/resume", (string id, StationManager manager) =>
{
    var station = manager.GetStation(id);
    if (station == null) return Results.NotFound(new { message = "Станция не найдена." });

    station.Resume();
    manager.RecordActivity("admin", station.Name, "▶ Эфир возобновлен");
    return Results.Ok(new { isPaused = false, message = "Воспроизведение возобновлено" });
});

// Переключение паузы
app.MapPost("/api/admin/stations/{id}/toggle-pause", (string id, StationManager manager) =>
{
    var station = manager.GetStation(id);
    if (station == null) return Results.NotFound(new { message = "Станция не найдена." });

    bool isPaused = station.TogglePause();
    manager.RecordActivity("admin", station.Name, isPaused ? "⏸ Эфир поставлен на паузу" : "▶ Эфир возобновлен");
    return Results.Ok(new { isPaused, message = isPaused ? "Эфир на паузе" : "Эфир возобновлен" });
});

// Перемотка трека (Seek)
app.MapPost("/api/admin/stations/{id}/seek", async (string id, HttpContext ctx, StationManager manager) =>
{
    var station = manager.GetStation(id);
    if (station == null) return Results.NotFound(new { message = "Станция не найдена." });

    double? seconds = null;
    double? delta = null;

    if (ctx.Request.HasJsonContentType())
    {
        try
        {
            var body = await ctx.Request.ReadFromJsonAsync<SeekRequest>();
            seconds = body?.Seconds;
            delta = body?.Delta;
        }
        catch { }
    }
    else if (ctx.Request.HasFormContentType)
    {
        var form = await ctx.Request.ReadFormAsync();
        if (double.TryParse(form["seconds"], System.Globalization.CultureInfo.InvariantCulture, out var s)) seconds = s;
        if (double.TryParse(form["delta"], System.Globalization.CultureInfo.InvariantCulture, out var d)) delta = d;
    }

    if (delta.HasValue)
    {
        station.SeekBy(delta.Value);
        manager.RecordActivity("admin", station.Name, $"⏩ Перемотка трека на {(delta.Value >= 0 ? "+" : "")}{delta.Value:F0} сек");
        return Results.Ok(new { success = true, remainingSeconds = station.TrackRemainingSeconds, durationSeconds = station.TrackDurationSeconds });
    }

    if (seconds.HasValue)
    {
        station.SeekTo(seconds.Value);
        manager.RecordActivity("admin", station.Name, $"⏩ Перемотка трека на {seconds.Value:F0} сек");
        return Results.Ok(new { success = true, remainingSeconds = station.TrackRemainingSeconds, durationSeconds = station.TrackDurationSeconds });
    }

    return Results.BadRequest(new { message = "Не указан параметр 'seconds' или 'delta'." });
});

// WebSocket эндпоинт прямого эфира (микрофон и студийный микшер DJ)
app.Map("/ws/stations/{id}/live-dj", async (string id, HttpContext ctx, StationManager manager) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = 400;
        return;
    }

    bool isAll = string.Equals(id, "all", StringComparison.OrdinalIgnoreCase);
    var targetStations = isAll
        ? manager.GetAllStations().ToList()
        : (manager.GetStation(id) is { } single ? new List<RadioStation> { single } : new List<RadioStation>());

    if (targetStations.Count == 0)
    {
        ctx.Response.StatusCode = 404;
        return;
    }

    using var webSocket = await ctx.WebSockets.AcceptWebSocketAsync();
    foreach (var st in targetStations)
    {
        st.SetLiveDj(true);
    }

    var clientIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "admin";
    var stationNameDesc = isAll ? "Все радиостанции" : targetStations[0].Name;
    manager.RecordActivity(clientIp, stationNameDesc, "🎙️ DJ вышел в прямой эфир (микрофон/микшер)");

    var buffer = new byte[8192];
    try
    {
        while (webSocket.State == System.Net.WebSockets.WebSocketState.Open)
        {
            var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
            {
                break;
            }

            if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Binary && result.Count > 0)
            {
                var chunk = new byte[result.Count];
                Buffer.BlockCopy(buffer, 0, chunk, 0, result.Count);

                var currentTargets = isAll ? manager.GetAllStations() : targetStations;
                foreach (var st in currentTargets)
                {
                    st.PushLiveAudio(chunk);
                }
                manager.AddStreamedBytes(result.Count);
            }
        }
    }
    catch
    {
        // Сессия прервана
    }
    finally
    {
        var currentTargets = isAll ? manager.GetAllStations() : targetStations;
        foreach (var st in currentTargets)
        {
            st.SetLiveDj(false);
        }
        manager.RecordActivity(clientIp, stationNameDesc, "⏹️ DJ завершил прямой эфир");
        try
        {
            if (webSocket.State == System.Net.WebSockets.WebSocketState.Open ||
                webSocket.State == System.Net.WebSockets.WebSocketState.CloseReceived)
            {
                await webSocket.CloseOutputAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "Closed", CancellationToken.None);
            }
        }
        catch { }
    }
});

app.MapPost("/api/admin/stations/{id}/reload", (string id, StationManager manager) =>
{
    var station = manager.GetStation(id);
    if (station == null) return Results.NotFound(new { message = "Станция не найдена." });

    station.RefreshPlaylist();
    manager.RecordActivity("admin", station.Name, "Плейлист обновлен с диска");
    return Results.Ok(new { message = "Плейлист успешно обновлен", trackCount = station.TrackCount });
});

app.MapPost("/api/admin/stations", async (HttpContext ctx, StationManager manager) =>
{
    string id = string.Empty;
    string name = string.Empty;

    if (ctx.Request.HasFormContentType)
    {
        var form = await ctx.Request.ReadFormAsync();
        id = form["id"].ToString();
        name = form["name"].ToString();
    }
    else
    {
        try
        {
            var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, string>>();
            if (body != null)
            {
                body.TryGetValue("id", out id!);
                body.TryGetValue("name", out name!);
            }
        }
        catch
        {
            return Results.BadRequest(new { message = "Некорректный формат данных запроса." });
        }
    }

    var (success, message) = manager.AddStation(id, name);
    if (!success)
        return Results.BadRequest(new { message });

    return Results.Ok(new { message });
});

app.MapPost("/api/admin/stations/{id}/upload", async (string id, HttpContext ctx, StationManager manager) =>
{
    var station = manager.GetStation(id);
    if (station == null) return Results.NotFound(new { message = "Станция не найдена." });

    if (!ctx.Request.HasFormContentType)
        return Results.BadRequest(new { message = "Ожидаются данные формы multipart/form-data." });

    var form = await ctx.Request.ReadFormAsync();
    var files = form.Files;

    if (files.Count == 0)
        return Results.BadRequest(new { message = "Файлы не выбраны." });

    int uploadedCount = 0;
    foreach (var file in files)
    {
        if (file.Length > 0 && (file.FileName.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) || 
                                file.FileName.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)))
        {
            var savePath = Path.Combine(station.DirectoryPath, Path.GetFileName(file.FileName));
            using (var stream = new FileStream(savePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
            {
                await file.CopyToAsync(stream);
            }
            uploadedCount++;
        }
    }

    station.RefreshPlaylist();
    manager.RecordActivity("admin", station.Name, $"Загружено {uploadedCount} аудиофайлов");

    return Results.Ok(new { message = $"Успешно загружено треков: {uploadedCount}", trackCount = station.TrackCount });
});

// Эндпоинт 2: Потоковое аудиовещание радиостанции в реальном времени
app.MapGet("/stream/{station}", async (string station, HttpContext ctx, StationManager manager, ILogger<Program> logger) =>
{
    // 1. Проверяем существование радиостанции
    var radioStation = manager.GetStation(station);
    if (radioStation == null)
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        await ctx.Response.WriteAsJsonAsync(new { message = $"Радиостанция '{station}' не найдена." });
        return;
    }

    // 2. Устанавливаем HTTP-заголовки для потоковой передачи данных (HTTP chunked streaming)
    ctx.Response.ContentType = "audio/mpeg";
    ctx.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
    ctx.Response.Headers.Pragma = "no-cache";
    ctx.Response.Headers.Expires = "0";
    ctx.Response.Headers["X-Accel-Buffering"] = "no";
    ctx.Response.Headers["Connection"] = "keep-alive";
    ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";

    // 3. Подключаем слушателя напрямую к живому серверному эфиру
    var (subId, reader, initialBurst) = radioStation.Subscribe();
    var clientIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    manager.RecordActivity(clientIp, radioStation.Name, "Подключился к эфиру");
    logger.LogInformation("Клиент [{Ip}] подключился к '{Station}'. Слушателей онлайн: {Count}", 
        clientIp, radioStation.Name, radioStation.ListenersCount);

    try
    {
        // 4. Отправляем текущий срез эфира (burst) для моментального старта воспроизведения в плеере
        if (initialBurst.Length > 0)
        {
            await ctx.Response.Body.WriteAsync(initialBurst, ctx.RequestAborted);
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
            manager.AddStreamedBytes(initialBurst.Length);
        }

        // 5. Транслируем живые порции звука по мере их воспроизведения на сервере
        await foreach (var chunk in reader.ReadAllAsync(ctx.RequestAborted))
        {
            await ctx.Response.Body.WriteAsync(chunk, ctx.RequestAborted);
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
            manager.AddStreamedBytes(chunk.Length);
        }
    }
    catch (OperationCanceledException)
    {
        logger.LogInformation("Клиент [{Ip}] отключился от станции '{Station}'.", clientIp, radioStation.Name);
    }
    catch (IOException ioEx)
    {
        logger.LogInformation("Сетевой обрыв соединения с клиентом [{Ip}] на станции '{Station}': {Message}", 
            clientIp, radioStation.Name, ioEx.Message);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Непредвиденная ошибка при вещании станции '{Station}' для клиента [{Ip}]", 
            radioStation.Name, clientIp);
    }
    finally
    {
        radioStation.Unsubscribe(subId);
        manager.RecordActivity(clientIp, radioStation.Name, "Отключился от эфира");
        logger.LogInformation("Слушатель отключен от '{Station}'. Осталось слушателей: {Count}", 
            radioStation.Name, radioStation.ListenersCount);
    }
});

app.Run();

public record SeekRequest(double? Seconds, double? Delta);

public partial class Program { }
