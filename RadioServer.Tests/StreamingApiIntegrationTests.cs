using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using RadioServer.Stations;
using Xunit;

namespace RadioServer.Tests;

public class StreamingApiIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public StreamingApiIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetStations_Returns_Successful_List()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/stations");

        response.EnsureSuccessStatusCode();
        var stations = await response.Content.ReadFromJsonAsync<List<StationInfo>>();

        Assert.NotNull(stations);
        Assert.NotEmpty(stations);
        Assert.Contains(stations, s => s.Id == "rock");
    }

    [Fact]
    public async Task GetAdminStats_Returns_Telemetry()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/admin/stats");

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("uptime", out _));
        Assert.True(root.TryGetProperty("memoryMb", out _));
        Assert.True(root.TryGetProperty("totalBytesSent", out _));
        Assert.True(root.TryGetProperty("stations", out var stationsProp));
        Assert.True(stationsProp.GetArrayLength() > 0);
    }

    [Fact]
    public async Task Admin_Station_Skip_And_Reload_Endpoints_Work()
    {
        var client = _factory.CreateClient();

        // 1. Skip на существующей станции
        var skipRes = await client.PostAsync("/api/admin/stations/rock/skip", null);
        skipRes.EnsureSuccessStatusCode();
        var skipJson = await skipRes.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.NotNull(skipJson);
        Assert.True(skipJson.ContainsKey("currentTrack"));

        // 2. Skip на несуществующей станции -> 404
        var nonExistentSkip = await client.PostAsync("/api/admin/stations/non_existent/skip", null);
        Assert.Equal(HttpStatusCode.NotFound, nonExistentSkip.StatusCode);

        // 3. Reload на существующей станции
        var reloadRes = await client.PostAsync("/api/admin/stations/rock/reload", null);
        reloadRes.EnsureSuccessStatusCode();

        // 4. Reload на несуществующей станции -> 404
        var nonExistentReload = await client.PostAsync("/api/admin/stations/non_existent/reload", null);
        Assert.Equal(HttpStatusCode.NotFound, nonExistentReload.StatusCode);
    }

    [Fact]
    public async Task Admin_Create_Station_Validates_Input()
    {
        var client = _factory.CreateClient();

        // Некорректный ID
        var badRes = await client.PostAsJsonAsync("/api/admin/stations", new { id = "../evil", name = "Evil Station" });
        Assert.Equal(HttpStatusCode.BadRequest, badRes.StatusCode);

        // Корректное создание
        var newId = "test_int_" + Guid.NewGuid().ToString("N")[..6];
        try
        {
            var okRes = await client.PostAsJsonAsync("/api/admin/stations", new { id = newId, name = "Integration Station" });
            okRes.EnsureSuccessStatusCode();

            // Проверяем, что станция появилась в /stations
            var stations = await client.GetFromJsonAsync<List<StationInfo>>("/stations");
            Assert.NotNull(stations);
            Assert.Contains(stations, s => s.Id == newId);
        }
        finally
        {
            try
            {
                var sm = _factory.Services.GetService(typeof(StationManager)) as StationManager;
                if (sm != null)
                {
                    sm.RemoveStation(newId);
                    Thread.Sleep(50);
                    var createdDir = Path.Combine(sm.AudioRootPath, newId);
                    if (Directory.Exists(createdDir))
                    {
                        Directory.Delete(createdDir, recursive: true);
                    }
                }
            }
            catch
            {
                // ignore
            }
        }
    }

    [Fact]
    public async Task StreamEndpoint_Returns_AudioMpeg_And_Streams_Data()
    {
        var client = _factory.CreateClient();

        // 1. Несуществующая станция -> 404
        var notFoundStream = await client.GetAsync("/stream/unknown_station");
        Assert.Equal(HttpStatusCode.NotFound, notFoundStream.StatusCode);

        // 2. Подключение к существующей станции
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var request = new HttpRequestMessage(HttpMethod.Get, "/stream/rock");
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

        response.EnsureSuccessStatusCode();
        Assert.Equal("audio/mpeg", response.Content.Headers.ContentType?.MediaType);

        // Читаем начальные данные (должен прийти burst первого трека)
        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        byte[] buffer = new byte[8192];
        int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cts.Token);

        Assert.True(bytesRead > 0);
    }

    [Fact]
    public async Task Stream_Tracks_Listeners_Accurately_On_Connect_And_Disconnect()
    {
        var client = _factory.CreateClient();

        // Получаем исходное количество слушателей
        var stationsBefore = await client.GetFromJsonAsync<List<StationInfo>>("/stations");
        var rockBefore = stationsBefore!.First(s => s.Id == "rock");
        int initialListeners = rockBefore.Listeners;

        // Открываем стрим
        using var cts = new CancellationTokenSource();
        var request = new HttpRequestMessage(HttpMethod.Get, "/stream/rock");
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        response.EnsureSuccessStatusCode();

        await using (var stream = await response.Content.ReadAsStreamAsync(cts.Token))
        {
            byte[] buf = new byte[1024];
            int read = await stream.ReadAsync(buf, 0, buf.Length, cts.Token);
            Assert.True(read > 0);

            // Проверяем, что слушатель зафиксирован на сервере
            var stationsDuring = await client.GetFromJsonAsync<List<StationInfo>>("/stations");
            var rockDuring = stationsDuring!.First(s => s.Id == "rock");
            Assert.Equal(initialListeners + 1, rockDuring.Listeners);

            // Клиент прерывает соединение (симуляция закрытия вкладки / смены станции)
            cts.Cancel();
        }

        // Ждем небольшую паузу для срабатывания finally на сервере
        await Task.Delay(200);

        var stationsAfter = await client.GetFromJsonAsync<List<StationInfo>>("/stations");
        var rockAfter = stationsAfter!.First(s => s.Id == "rock");
        Assert.Equal(initialListeners, rockAfter.Listeners);
    }

    [Fact]
    public async Task Concurrent_Streams_Receive_Data_Independently()
    {
        var client = _factory.CreateClient();

        var tasks = Enumerable.Range(0, 3).Select(async i =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var response = await client.SendAsync(
                new HttpRequestMessage(HttpMethod.Get, "/stream/jazz"),
                HttpCompletionOption.ResponseHeadersRead,
                cts.Token
            );

            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            byte[] buf = new byte[4096];
            int read = await stream.ReadAsync(buf, 0, buf.Length, cts.Token);
            return read;
        });

        var results = await Task.WhenAll(tasks);
        foreach (var bytesRead in results)
        {
            Assert.True(bytesRead > 0);
        }
    }

    [Fact]
    public async Task Stream_Seamlessly_Continues_Reading_When_Skip_Is_Triggered()
    {
        var client = _factory.CreateClient();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        var response = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "/stream/pop"),
            HttpCompletionOption.ResponseHeadersRead,
            cts.Token
        );
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        byte[] buf = new byte[4096];

        // 1. Читаем данные до пропуска
        int read1 = await stream.ReadAsync(buf, 0, buf.Length, cts.Token);
        Assert.True(read1 > 0);

        // 2. Вызываем skip в админке
        var skipRes = await client.PostAsync("/api/admin/stations/pop/skip", null);
        skipRes.EnsureSuccessStatusCode();

        // 3. Стрим не оборвался и продолжает отдавать байты уже нового трека!
        int read2 = await stream.ReadAsync(buf, 0, buf.Length, cts.Token);
        Assert.True(read2 > 0);
    }

    [Fact]
    public async Task Static_Files_And_Redirects_Are_Served_Correctly()
    {
        var client = _factory.CreateClient();

        // 1. GET /index.html
        var indexRes = await client.GetAsync("/index.html");
        Assert.Equal(HttpStatusCode.OK, indexRes.StatusCode);

        // 2. GET /admin redirect
        var adminRedirectClient = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var adminRes = await adminRedirectClient.GetAsync("/admin");
        Assert.Equal(HttpStatusCode.Redirect, adminRes.StatusCode);
        Assert.Equal("/admin.html", adminRes.Headers.Location?.OriginalString);

        // 3. GET /admin.html
        var adminHtmlRes = await client.GetAsync("/admin.html");
        Assert.Equal(HttpStatusCode.OK, adminHtmlRes.StatusCode);

        // 4. CSS and JS files
        var styleRes = await client.GetAsync("/style.css");
        Assert.Equal(HttpStatusCode.OK, styleRes.StatusCode);

        var playerJsRes = await client.GetAsync("/player.js");
        Assert.Equal(HttpStatusCode.OK, playerJsRes.StatusCode);

        var adminCssRes = await client.GetAsync("/admin.css");
        Assert.Equal(HttpStatusCode.OK, adminCssRes.StatusCode);

        var adminJsRes = await client.GetAsync("/admin.js");
        Assert.Equal(HttpStatusCode.OK, adminJsRes.StatusCode);
    }

    [Fact]
    public async Task UI_Meets_Palette_And_Clean_Interface_Requirements()
    {
        var client = _factory.CreateClient();

        // 1. style.css: не содержит синих цветов
        var styleCss = await client.GetStringAsync("/style.css");
        Assert.DoesNotContain("#58a6ff", styleCss, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("#79b8ff", styleCss, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rgba(88, 166, 255", styleCss, StringComparison.OrdinalIgnoreCase);

        // 2. admin.css: не содержит синих цветов
        var adminCss = await client.GetStringAsync("/admin.css");
        Assert.DoesNotContain("#58a6ff", adminCss, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("#79b8ff", adminCss, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rgba(88, 166, 255", adminCss, StringComparison.OrdinalIgnoreCase);

        // 3. index.html: нет академических подписей в подвале и жестко прописанного синего цвета
        var indexHtml = await client.GetStringAsync("/index.html");
        Assert.DoesNotContain("Лабораторная работа", indexHtml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rgba(88, 166, 255", indexHtml, StringComparison.OrdinalIgnoreCase);

        // 4. admin.html: нет подзаголовка/описания под названием
        var adminHtml = await client.GetStringAsync("/admin.html");
        Assert.DoesNotContain("Панель мониторинга и управления", adminHtml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<p class=\"subtitle\">", adminHtml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Pop_Station_Reports_Accurate_Track_Duration_For_Madeon()
    {
        var client = _factory.CreateClient();

        // Небольшая задержка, чтобы фоновый вещатель успел разобрать метаданные трека
        await Task.Delay(300);

        var stations = await client.GetFromJsonAsync<List<StationInfo>>("/stations");
        Assert.NotNull(stations);

        var pop = stations.FirstOrDefault(s => s.Id == "pop");
        Assert.NotNull(pop);

        if (pop.CurrentTrack.Contains("Madeon", StringComparison.OrdinalIgnoreCase))
        {
            // Длительность должна быть ~230 сек (~3 мин 50 сек), а НЕ 497 сек!
            Assert.InRange(pop.TrackDurationSeconds, 228, 235);
            Assert.InRange(pop.TrackRemainingSeconds, 1, 235);
        }
    }

    [Fact]
    public async Task Admin_Can_Pause_Resume_And_Seek_Station()
    {
        var client = _factory.CreateClient();

        // 1. Pause
        var pauseRes = await client.PostAsync("/api/admin/stations/rock/pause", null);
        pauseRes.EnsureSuccessStatusCode();

        var stations = await client.GetFromJsonAsync<List<StationInfo>>("/stations");
        Assert.NotNull(stations);
        var rock = stations.First(s => s.Id == "rock");
        Assert.True(rock.IsPaused);

        // 2. Resume
        var resumeRes = await client.PostAsync("/api/admin/stations/rock/resume", null);
        resumeRes.EnsureSuccessStatusCode();

        stations = await client.GetFromJsonAsync<List<StationInfo>>("/stations");
        rock = stations!.First(s => s.Id == "rock");
        Assert.False(rock.IsPaused);

        // 3. Toggle-pause
        var toggleRes = await client.PostAsync("/api/admin/stations/rock/toggle-pause", null);
        toggleRes.EnsureSuccessStatusCode();

        stations = await client.GetFromJsonAsync<List<StationInfo>>("/stations");
        rock = stations!.First(s => s.Id == "rock");
        Assert.True(rock.IsPaused);

        // Toggle back to unpause
        await client.PostAsync("/api/admin/stations/rock/toggle-pause", null);

        // 4. Seek (seconds)
        var seekRes = await client.PostAsJsonAsync("/api/admin/stations/rock/seek", new { seconds = 2.0 });
        seekRes.EnsureSuccessStatusCode();

        // 5. Seek (delta)
        var seekDeltaRes = await client.PostAsJsonAsync("/api/admin/stations/rock/seek", new { delta = 1.0 });
        seekDeltaRes.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Stream_Endpoint_While_Paused_Returns_Valid_Silence_Stream()
    {
        var client = _factory.CreateClient();

        // 1. Ставим станцию на паузу
        var pauseRes = await client.PostAsync("/api/admin/stations/jazz/pause", null);
        pauseRes.EnsureSuccessStatusCode();

        try
        {
            // 2. Слушатель подключается к станции на паузе
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            var response = await client.SendAsync(
                new HttpRequestMessage(HttpMethod.Get, "/stream/jazz"),
                HttpCompletionOption.ResponseHeadersRead,
                cts.Token
            );
            response.EnsureSuccessStatusCode();
            Assert.Equal("audio/mpeg", response.Content.Headers.ContentType?.MediaType);

            // 3. Читаем начальные данные (должен прийти burst тишины ~62 КБ)
            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            byte[] buffer = new byte[8192];
            int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cts.Token);

            Assert.True(bytesRead > 0);
            // Проверяем валидность заголовка MP3 фрейма тишины
            Assert.Equal(0xFF, buffer[0]);
            Assert.Equal(0xFB, buffer[1]);
            Assert.Equal(0x90, buffer[2]);
            Assert.Equal(0x64, buffer[3]);

            // 4. Возобновляем воспроизведение
            var resumeRes = await client.PostAsync("/api/admin/stations/jazz/resume", null);
            resumeRes.EnsureSuccessStatusCode();

            // 5. Поток продолжает отдавать данные
            int nextRead = await stream.ReadAsync(buffer, 0, buffer.Length, cts.Token);
            Assert.True(nextRead > 0);
        }
        finally
        {
            // Обязательно снимаем с паузы
            await client.PostAsync("/api/admin/stations/jazz/resume", null);
        }
    }

    [Fact]
    public async Task CORS_Headers_Are_Present_On_Stream_And_Static_Files()
    {
        var client = _factory.CreateClient();

        // 1. Проверяем заголовки на /stream/rock
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var streamRes = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "/stream/rock"),
            HttpCompletionOption.ResponseHeadersRead,
            cts.Token
        );
        streamRes.EnsureSuccessStatusCode();
        Assert.True(streamRes.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Equal("*", streamRes.Headers.GetValues("Access-Control-Allow-Origin").First());

        // 2. Проверяем заголовки на статических файлах
        var staticRes = await client.GetAsync("/style.css");
        staticRes.EnsureSuccessStatusCode();
        Assert.True(staticRes.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Equal("*", staticRes.Headers.GetValues("Access-Control-Allow-Origin").First());
    }

    [Fact]
    public async Task UI_Title_And_Brand_Name_Is_Simply_Radio()
    {
        var client = _factory.CreateClient();

        // 1. index.html title and h1
        var indexHtml = await client.GetStringAsync("/index.html");
        Assert.Contains("<title>Радио</title>", indexHtml);
        Assert.Contains("<h1>Радио</h1>", indexHtml);
        Assert.Contains("<span class=\"footer-brand\">Радио</span>", indexHtml);

        // 2. admin.html title and h1
        var adminHtml = await client.GetStringAsync("/admin.html");
        Assert.Contains("<title>Радио</title>", adminHtml);
        Assert.Contains("<h1>Радио</h1>", adminHtml);
        Assert.Contains("value=\"all\"", adminHtml); // Селектор вещания на все станции

        // 3. player.js
        var playerJs = await client.GetStringAsync("/player.js");
        Assert.Contains("album: 'Радио'", playerJs);
    }

    [Fact]
    public async Task WebSocket_LiveDj_Can_Broadcast_To_Specific_Station_And_All_Stations()
    {
        var wsClient = _factory.Server.CreateWebSocketClient();
        var sm = _factory.Services.GetRequiredService<StationManager>();

        var rock = sm.GetStation("rock");
        var pop = sm.GetStation("pop");
        Assert.NotNull(rock);
        Assert.NotNull(pop);

        // 1. Подключение к конкретной радиостанции (rock)
        using var cts1 = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var rockWs = await wsClient.ConnectAsync(new Uri("ws://localhost/ws/stations/rock/live-dj"), cts1.Token);
        Assert.Equal(System.Net.WebSockets.WebSocketState.Open, rockWs.State);

        await Task.Delay(100);
        Assert.True(rock.IsLiveDj);
        Assert.False(pop.IsLiveDj);

        // Отправка бинарного чанка
        byte[] dummyChunk = new byte[] { 0xFF, 0xFB, 0x90, 0x64, 0x00, 0x00 };
        await rockWs.SendAsync(new ArraySegment<byte>(dummyChunk), System.Net.WebSockets.WebSocketMessageType.Binary, true, cts1.Token);

        await rockWs.CloseOutputAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "Done", cts1.Token);
        await Task.Delay(100);
        Assert.False(rock.IsLiveDj);

        // 2. Подключение ко всем станциям (all)
        using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var allWs = await wsClient.ConnectAsync(new Uri("ws://localhost/ws/stations/all/live-dj"), cts2.Token);
        Assert.Equal(System.Net.WebSockets.WebSocketState.Open, allWs.State);

        await Task.Delay(100);
        foreach (var station in sm.GetAllStations())
        {
            Assert.True(station.IsLiveDj, $"Станция {station.Id} должна иметь IsLiveDj == true при вещании на 'all'");
        }

        await allWs.SendAsync(new ArraySegment<byte>(dummyChunk), System.Net.WebSockets.WebSocketMessageType.Binary, true, cts2.Token);

        await allWs.CloseOutputAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "Done", cts2.Token);
        await Task.Delay(100);
        foreach (var station in sm.GetAllStations())
        {
            Assert.False(station.IsLiveDj, $"Станция {station.Id} должна вернуть IsLiveDj == false после закрытия сокета");
        }
    }

    [Fact]
    public async Task Stream_Endpoint_Allows_Repeated_Reconnection_While_Station_Is_Paused()
    {
        var client = _factory.CreateClient();

        // Ставим станцию rock на паузу
        var pauseRes = await client.PostAsync("/api/admin/stations/rock/pause", null);
        pauseRes.EnsureSuccessStatusCode();

        try
        {
            for (int i = 0; i < 3; i++)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                var response = await client.SendAsync(
                    new HttpRequestMessage(HttpMethod.Get, $"/stream/rock?reconnect={i}"),
                    HttpCompletionOption.ResponseHeadersRead,
                    cts.Token
                );

                response.EnsureSuccessStatusCode();
                Assert.Equal("audio/mpeg", response.Content.Headers.ContentType?.MediaType);

                await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
                byte[] buf = new byte[4096];
                int read = await stream.ReadAsync(buf, 0, buf.Length, cts.Token);
                Assert.True(read > 0);

                // Заголовок тишины
                Assert.Equal(0xFF, buf[0]);
                Assert.Equal(0xFB, buf[1]);
            }

            // Проверяем, что /stations остается доступным и не блокируется
            var stationsRes = await client.GetAsync("/stations");
            stationsRes.EnsureSuccessStatusCode();
            var stations = await stationsRes.Content.ReadFromJsonAsync<List<StationInfo>>();
            Assert.NotNull(stations);
            var rock = stations.First(s => s.Id == "rock");
            Assert.True(rock.IsPaused);
        }
        finally
        {
            await client.PostAsync("/api/admin/stations/rock/resume", null);
        }
    }

    [Fact]
    public async Task LiveDj_Stream_Delivers_Microphone_Audio_To_Listener_In_RealTime()
    {
        var client = _factory.CreateClient();
        var wsClient = _factory.Server.CreateWebSocketClient();

        // 1. Слушатель подключается к стриму станции pop
        using var streamCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var streamRes = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "/stream/pop"),
            HttpCompletionOption.ResponseHeadersRead,
            streamCts.Token
        );
        streamRes.EnsureSuccessStatusCode();
        await using var audioStream = await streamRes.Content.ReadAsStreamAsync(streamCts.Token);

        // Читаем начальный burst
        byte[] readBuf = new byte[8192];
        int initialRead = await audioStream.ReadAsync(readBuf, 0, readBuf.Length, streamCts.Token);
        Assert.True(initialRead > 0);

        // 2. DJ выходит в прямой эфир через WebSocket на станцию pop
        using var wsCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var djWs = await wsClient.ConnectAsync(new Uri("ws://localhost/ws/stations/pop/live-dj"), wsCts.Token);
        Assert.Equal(System.Net.WebSockets.WebSocketState.Open, djWs.State);

        await Task.Delay(50);

        // 3. DJ передает уникальный аудиопакет с сигнатурой "DJ_VOICE"
        byte[] djVoicePacket = new byte[417];
        djVoicePacket[0] = 0xFF;
        djVoicePacket[1] = 0xFB;
        djVoicePacket[2] = 0x90;
        djVoicePacket[3] = 0x64;
        byte[] sig = System.Text.Encoding.ASCII.GetBytes("DJ_VOICE");
        Buffer.BlockCopy(sig, 0, djVoicePacket, 4, sig.Length);

        // Отправляем пакеты голоса DJ несколько раз
        for (int i = 0; i < 3; i++)
        {
            await djWs.SendAsync(new ArraySegment<byte>(djVoicePacket), System.Net.WebSockets.WebSocketMessageType.Binary, true, wsCts.Token);
            await Task.Delay(30);
        }

        // 4. Слушатель должен получить данные в стриме
        bool foundVoiceSignature = false;
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline && !foundVoiceSignature)
        {
            int bytesRead = await audioStream.ReadAsync(readBuf, 0, readBuf.Length, streamCts.Token);
            if (bytesRead > 0)
            {
                // Ищем сигнатуру "DJ_VOICE" в прочитанных байтах
                var span = readBuf.AsSpan(0, bytesRead);
                if (span.IndexOf(sig) >= 0)
                {
                    foundVoiceSignature = true;
                    break;
                }
            }
        }

        Assert.True(foundVoiceSignature, "Голос ведущего (сигнатура DJ_VOICE) должен быть доставлен слушателю стрима в реальном времени!");

        // 5. Завершение эфира
        await djWs.CloseOutputAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "Done", wsCts.Token);
    }

    [Fact]
    public async Task LiveDj_Broadcast_To_All_Stations_Delivers_Voice_To_Multiple_Station_Listeners()
    {
        var client = _factory.CreateClient();
        var wsClient = _factory.Server.CreateWebSocketClient();

        // 1. Два слушателя подключаются к разным станциям: rock и jazz
        using var rockCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var jazzCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        var rockRes = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/stream/rock"), HttpCompletionOption.ResponseHeadersRead, rockCts.Token);
        var jazzRes = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/stream/jazz"), HttpCompletionOption.ResponseHeadersRead, jazzCts.Token);
        rockRes.EnsureSuccessStatusCode();
        jazzRes.EnsureSuccessStatusCode();

        await using var rockStream = await rockRes.Content.ReadAsStreamAsync(rockCts.Token);
        await using var jazzStream = await jazzRes.Content.ReadAsStreamAsync(jazzCts.Token);

        byte[] buf = new byte[8192];
        _ = await rockStream.ReadAsync(buf, 0, buf.Length, rockCts.Token);
        _ = await jazzStream.ReadAsync(buf, 0, buf.Length, jazzCts.Token);

        // 2. DJ подключается к "all" (вещание на все станции)
        using var wsCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var allWs = await wsClient.ConnectAsync(new Uri("ws://localhost/ws/stations/all/live-dj"), wsCts.Token);
        Assert.Equal(System.Net.WebSockets.WebSocketState.Open, allWs.State);

        await Task.Delay(50);

        byte[] allVoicePacket = new byte[417];
        allVoicePacket[0] = 0xFF;
        allVoicePacket[1] = 0xFB;
        allVoicePacket[2] = 0x90;
        allVoicePacket[3] = 0x64;
        byte[] sig = System.Text.Encoding.ASCII.GetBytes("ALL_STATIONS_DJ");
        Buffer.BlockCopy(sig, 0, allVoicePacket, 4, sig.Length);

        for (int i = 0; i < 4; i++)
        {
            await allWs.SendAsync(new ArraySegment<byte>(allVoicePacket), System.Net.WebSockets.WebSocketMessageType.Binary, true, wsCts.Token);
            await Task.Delay(30);
        }

        // 3. Проверяем, что оба слушателя на разных станциях получили голос DJ
        bool rockReceived = false;
        bool jazzReceived = false;
        var deadline = DateTime.UtcNow.AddSeconds(3);

        byte[] rockReadBuf = new byte[8192];
        byte[] jazzReadBuf = new byte[8192];

        while (DateTime.UtcNow < deadline && (!rockReceived || !jazzReceived))
        {
            if (!rockReceived)
            {
                int rRead = await rockStream.ReadAsync(rockReadBuf, 0, rockReadBuf.Length, rockCts.Token);
                if (rRead > 0 && rockReadBuf.AsSpan(0, rRead).IndexOf(sig) >= 0)
                    rockReceived = true;
            }
            if (!jazzReceived)
            {
                int jRead = await jazzStream.ReadAsync(jazzReadBuf, 0, jazzReadBuf.Length, jazzCts.Token);
                if (jRead > 0 && jazzReadBuf.AsSpan(0, jRead).IndexOf(sig) >= 0)
                    jazzReceived = true;
            }
        }

        Assert.True(rockReceived, "Слушатель станции Rock должен получить общий голос DJ!");
        Assert.True(jazzReceived, "Слушатель станции Jazz должен получить общий голос DJ!");

        await allWs.CloseOutputAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "Done", wsCts.Token);
    }
}
