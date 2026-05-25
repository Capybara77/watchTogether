ARG PLAYWRIGHT_VERSION=1.52.0

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY WatchTogether.csproj ./
RUN dotnet restore

COPY . ./
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/playwright/dotnet:v${PLAYWRIGHT_VERSION}-noble AS final
WORKDIR /app

USER root
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg pulseaudio xvfb \
    && rm -rf /var/lib/apt/lists/*

ENV ASPNETCORE_URLS=http://+:8080
ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright

COPY --from=build /app/publish .

EXPOSE 8080
ENTRYPOINT ["dotnet", "WatchTogether.dll"]
