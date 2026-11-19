FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
WORKDIR /src
COPY WorkoutService/WorkoutService.csproj ./WorkoutService/
COPY Shared/Shared.csproj ./Shared/
RUN dotnet restore ./WorkoutService/WorkoutService.csproj
COPY WorkoutService/ ./WorkoutService/
COPY Shared/ ./Shared/
RUN dotnet publish ./WorkoutService/WorkoutService.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:11.0
WORKDIR /app
COPY --from=build /app .
# The image's non-root user owns the default data directory. Compose runs the
# container as the host user that owns the bind-mounted ./workout-data.
RUN mkdir -p /data && chown "$APP_UID" /data
USER $APP_UID
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
VOLUME /data
HEALTHCHECK --interval=60s --timeout=10s --start-period=30s --retries=3 \
    CMD ["dotnet", "/app/WorkoutService.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "WorkoutService.dll"]
