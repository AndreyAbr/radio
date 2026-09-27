# Сервер интернет-радио на ASP.NET Core

Сервер потокового аудиовещания (HTTP Streaming) на базе ASP.NET Core Minimal API со встроенным веб-плеером и панелью администратора.

## Требования

* .NET SDK 10.0 или новее (или Docker)

## Запуск проекта

### Вариант 1. Запуск через .NET CLI

1. Перейдите в каталог проекта:
   ```bash
   cd radio
   ```

2. Запустите сервер:
   ```bash
   dotnet run --urls "http://localhost:5000"
   ```

### Вариант 2. Запуск в Docker

```bash
docker compose up -d --build
```

Сервер будет доступен по адресу `http://localhost:5000`.

## Веб-интерфейсы

* Веб-плеер: [http://localhost:5000](http://localhost:5000)
* Панель администратора: [http://localhost:5000/admin](http://localhost:5000/admin)

## Основные эндпоинты

* `GET /` — веб-плеер слушателя.
* `GET /admin` — панель администратора (управление треками, лимиты, метрики).
* `GET /stream/{station}` — аудиопоток станции (`rock`, `jazz`, `pop`, `synthwave`). Поддерживает заголовок `Icy-MetaData: 1` для передачи названий треков (Shoutcast/Icecast).
* `GET /stations` — список доступных станций и метаданные в JSON.
* `GET /api/admin/stations/{id}/tracks` — список треков станции.
* `DELETE /api/admin/stations/{id}/tracks/{fileName}` — удаление трека станции.
* `POST /api/admin/stations/{id}/max-listeners` — установка лимита слушателей (при превышении сервер возвращает HTTP 503 Service Unavailable).

## Запуск тестов

```bash
dotnet test RadioServer.Tests/RadioServer.Tests.csproj
```
