FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["MovieStoreShowcase.csproj", "./"]
RUN dotnet restore "MovieStoreShowcase.csproj"
COPY . .
RUN dotnet publish -c Release -o /app/publish

# Fetch a static, self-contained ffmpeg build in its own isolated stage -
# never apt-get installed into the aspnet runtime image itself (that broke
# the .NET runtime with a segfault on a previous deploy).
#
# Using BtbN's "gpl" static build here instead of the johnvansickle one:
# the johnvansickle build turned out not to include the `drawtext` filter
# (needed for the title text overlay), which made every trailer request
# fail with "No such filter: 'drawtext'". BtbN's gpl build bundles
# freetype/fontconfig properly and includes the full filter set.
FROM debian:bookworm-slim AS ffmpeg
RUN apt-get update && \
    apt-get install -y --no-install-recommends ca-certificates curl xz-utils && \
    curl -L https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-linux64-gpl.tar.xz -o /tmp/ffmpeg.tar.xz && \
    tar -xf /tmp/ffmpeg.tar.xz -C /tmp && \
    mv /tmp/ffmpeg-master-latest-linux64-gpl/bin/ffmpeg /usr/local/bin/ffmpeg && \
    mv /tmp/ffmpeg-master-latest-linux64-gpl/bin/ffprobe /usr/local/bin/ffprobe && \
    rm -rf /tmp/ffmpeg*

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app

# Just copying two binary files in - the aspnet base image's own packages
# are never touched, so there's nothing for it to conflict with.
COPY --from=ffmpeg /usr/local/bin/ffmpeg /usr/local/bin/ffmpeg
COPY --from=ffmpeg /usr/local/bin/ffprobe /usr/local/bin/ffprobe

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "MovieStoreShowcase.dll"]