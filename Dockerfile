# Multi-stage Dockerfile for RadioServer (.NET 10)
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Копируем файл проекта и восстанавливаем зависимости
COPY ["RadioServer.csproj", "./"]
RUN dotnet restore "RadioServer.csproj"

# Копируем остальной исходный код и публикуем Release-сборку
COPY . .
RUN dotnet publish "RadioServer.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Финальный легковесный runtime-образ
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish .

# Настройка порта и сетевого окружения
ENV ASPNETCORE_URLS=http://+:5000
EXPOSE 5000

# Точка входа приложения
ENTRYPOINT ["dotnet", "RadioServer.dll"]
