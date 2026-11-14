FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
WORKDIR /src
COPY WorkoutService.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:11.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
VOLUME /data
ENTRYPOINT ["dotnet", "WorkoutService.dll"]
