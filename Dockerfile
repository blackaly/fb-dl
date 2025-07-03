FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /app

COPY ./src/fb-dl/fb-dl.csproj ./src/fb-dl/
RUN dotnet restore ./src/fb-dl/fb-dl.csproj

COPY . .

RUN dotnet publish ./src/fb-dl/fb-dl.csproj -c Release -o /app/out

FROM mcr.microsoft.com/dotnet/runtime:9.0
WORKDIR /app

COPY --from=build /app/out .

ENTRYPOINT ["dotnet", "fb-dl.dll"]
