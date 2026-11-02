FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/ src/
RUN dotnet publish src/Dsp.DevOverview.Api/Dsp.DevOverview.Api.csproj -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out/ ./
COPY architecture/ /repo/architecture/
COPY catalog/ /repo/catalog/
COPY data/ /repo/data/
ENV ASPNETCORE_ENVIRONMENT=Production Overview__Root=/repo
EXPOSE 10000
ENTRYPOINT ["sh","-c","ASPNETCORE_URLS=http://0.0.0.0:${PORT:-10000} exec dotnet Dsp.DevOverview.Api.dll --urls http://0.0.0.0:${PORT:-10000}"]
