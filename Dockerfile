FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["MovieStoreShowcase.csproj", "./"]
RUN dotnet restore "MovieStoreShowcase.csproj"
COPY . .
RUN dotnet publish -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app

# Install ffmpeg in the Docker container
RUN apt-get update && apt-get install -y ffmpeg && rm -rf /var/lib/apt/lists/*

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV DOTNET_EnableDiagnostics=0
ENV COMPlus_TieredCompilation=0

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "MovieStoreShowcase.dll"]