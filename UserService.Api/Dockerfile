# ---- build aşaması ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# önce sadece csproj → restore katmanı cache'lenir
COPY *.csproj ./
RUN dotnet restore

# sonra kaynak kod → publish
COPY . ./
RUN dotnet publish -c Release -o /app --no-restore

# ---- çalışma aşaması ----
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app ./

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "UserService.Api.dll"]
