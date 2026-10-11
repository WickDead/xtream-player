FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY Directory.Build.props Directory.Packages.props ./
COPY src/XtreamPlayer.Core/ src/XtreamPlayer.Core/
COPY src/XtreamPlayer.Application/ src/XtreamPlayer.Application/
COPY src/XtreamPlayer.Infrastructure/ src/XtreamPlayer.Infrastructure/
COPY src/XtreamPlayer.Server/ src/XtreamPlayer.Server/
RUN dotnet publish src/XtreamPlayer.Server -c Release -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
USER root
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg curl ca-certificates fonts-dejavu-core && rm -rf /var/lib/apt/lists/* && mkdir -p /data && chown app:app /data
WORKDIR /app
COPY --from=build /out .
ENV ASPNETCORE_URLS=http://+:8080 \
    Server__DataDirectory=/data \
    Server__InternalUrl=http://127.0.0.1:8080 \
    XDG_DATA_HOME=/data
USER app
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s CMD curl --fail --silent http://localhost:8080/health > /dev/null || exit 1
ENTRYPOINT ["dotnet", "XtreamPlayer.Server.dll"]
