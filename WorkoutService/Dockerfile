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
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
VOLUME /data
ENTRYPOINT ["dotnet", "WorkoutService.dll"]
