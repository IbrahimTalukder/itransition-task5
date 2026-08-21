FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["MovieStoreShowcase.csproj", "./"]
RUN dotnet restore "MovieStoreShowcase.csproj"
COPY . .
RUN dotnet publish -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app

# The trailer generator shells out to the `ffmpeg` binary (gradient scenes,
# Ken Burns zoom on AI images, xfade transitions, title text, audio mix) -
# the base aspnet image doesn't include it, so without this the app runs
# fine but every trailer/poster/export request fails with "command not
# found" (or an ffmpeg-related crash) as soon as it's actually used.
RUN apt-get update && \
    apt-get install -y --no-install-recommends ffmpeg && \
    rm -rf /var/lib/apt/lists/*

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "MovieStoreShowcase.dll"]