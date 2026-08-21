FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["MovieStoreShowcase.csproj", "./"]
RUN dotnet restore "MovieStoreShowcase.csproj"
COPY . .
RUN dotnet publish -c Release -o /app/publish

# Fetch a static, self-contained ffmpeg build in its own isolated stage.
# We deliberately do NOT apt-get install ffmpeg into the aspnet runtime
# image below - doing that pulls in newer shared libraries (libssl, etc.)
# that conflict with what the .NET runtime was built against, which is
# what caused the previous deploy to crash with a segfault (exit 139) as
# soon as the container started. A static binary has no such dependency -
# it's just a file we copy in.
FROM debian:bookworm-slim AS ffmpeg
RUN apt-get update && \
    apt-get install -y --no-install-recommends ca-certificates curl xz-utils && \
    curl -L https://johnvansickle.com/ffmpeg/releases/ffmpeg-release-amd64-static.tar.xz -o /tmp/ffmpeg.tar.xz && \
    tar -xf /tmp/ffmpeg.tar.xz -C /tmp && \
    mv /tmp/ffmpeg-*-amd64-static/ffmpeg /usr/local/bin/ffmpeg && \
    mv /tmp/ffmpeg-*-amd64-static/ffprobe /usr/local/bin/ffprobe && \
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