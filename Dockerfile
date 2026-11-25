# Build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Juscraper.Core/Juscraper.Core.csproj src/Juscraper.Core/
COPY src/Juscraper.Api/Juscraper.Api.csproj src/Juscraper.Api/
RUN dotnet restore src/Juscraper.Api/Juscraper.Api.csproj
COPY src/ src/
RUN dotnet publish src/Juscraper.Api/Juscraper.Api.csproj -c Release -o /app --no-restore

# Runtime: imagem "chiseled" (sem shell nem gerenciador de pacotes), usuário não-root por padrão.
# A variante -extra traz ICU e tzdata: o fuso America/Sao_Paulo e o pt-BR são necessários.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Juscraper.Api.dll"]
