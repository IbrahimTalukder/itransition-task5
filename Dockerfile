FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["MovieStoreShowcase.csproj", "./"]
RUN dotnet restore "MovieStoreShowcase.csproj"
COPY . .
RUN dotnet publish -c Release -o /app/publish

# Fetch a static, self-contained ffmpeg build (with drawtext support) and a
# real .ttf font in one isolated stage - never apt-get installed into the
# aspnet runtime image itself (that broke the .NET runtime with a segfault
# on an earlier attempt).
FROM debian:bookworm-slim AS ffmpeg
RUN apt-get update && \
    apt-get install -y --no-install-recommends ca-certificates curl xz-utils fonts-dejavu-core && \
    curl -L https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-linux64-gpl.tar.xz -o /tmp/ffmpeg.tar.xz && \
    tar -xf /tmp/ffmpeg.tar.xz -C /tmp && \
    mv /tmp/ffmpeg-master-latest-linux64-gpl/bin/ffmpeg /usr/local/bin/ffmpeg && \
    mv /tmp/ffmpeg-master-latest-linux64-gpl/bin/ffprobe /usr/local/bin/ffprobe && \
    rm -rf /tmp/ffmpeg*

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app

# Just copying files in (two binaries + one font) - the aspnet base image's
# own packages are never touched, so there's nothing for it to conflict with.
COPY --from=ffmpeg /usr/local/bin/ffmpeg /usr/local/bin/ffmpeg
COPY --from=ffmpeg /usr/local/bin/ffprobe /usr/local/bin/ffprobe
COPY --from=ffmpeg /usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf /usr/share/fonts/dejavu/DejaVuSans-Bold.ttf

# Only set inside the container - TrailerGeneratorService.cs checks for this
# and falls back to ffmpeg's own default-font lookup (what already works on
# local/Windows dev) when it's unset, so this doesn't change local behavior.
ENV FFMPEG_FONT_FILE=/usr/share/fonts/dejavu/DejaVuSans-Bold.ttf

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "MovieStoreShowcase.dll"]