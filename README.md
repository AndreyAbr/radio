# Сервер интернет-радио на ASP.NET Core

Сервер потокового аудиовещания (HTTP Streaming) на базе ASP.NET Core Minimal API со встроенным веб-плеером и панелью администратора.

## Требования

* .NET SDK 10.0 или новее

## Запуск проекта

1. Перейдите в каталог проекта:
   ```bash
   cd radio
   ```

2. Запустите сервер:
   ```bash
   dotnet run --urls "http://localhost:5000"
   ```

3. Откройте интерфейс в браузере:
   * Веб-плеер: [http://localhost:5000](http://localhost:5000)
   * Панель администратора: [http://localhost:5000/admin](http://localhost:5000/admin)

## Основные адреса

* `GET /` — веб-плеер слушателя.
* `GET /admin` — панель управления (переключение станций, загрузка треков, метрики).
* `GET /stream/{station}` — аудиопоток станции (`rock`, `jazz`, `pop`).
* `GET /stations` — список доступных станций и метаданные в JSON.

## Запуск тестов

```bash
dotnet test RadioServer.Tests/RadioServer.Tests.csproj
```
