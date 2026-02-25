# 使用微软官方的 .NET 8 SDK 镜像进行编译
FROM [mcr.microsoft.com/dotnet/sdk:8.0](https://mcr.microsoft.com/dotnet/sdk:8.0) AS build
WORKDIR /src
COPY . .
RUN dotnet restore "AstrologyAPI.csproj"
RUN dotnet publish "AstrologyAPI.csproj" -c Release -o /app/publish

# 使用轻量级的 ASP.NET 运行环境
FROM [mcr.microsoft.com/dotnet/aspnet:8.0](https://mcr.microsoft.com/dotnet/aspnet:8.0) AS final
WORKDIR /app
COPY --from=build /app/publish .

# 暴露端口给 Render
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["dotnet", "AstrologyAPI.dll"]
