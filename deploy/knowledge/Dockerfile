FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY publish/ .
ENV ASPNETCORE_URLS=http://+:8080
USER 1654:1654
ENTRYPOINT ["dotnet", "ChronoIsle.Knowledge.Server.dll"]
