FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS build
WORKDIR /src
COPY ["Kinetix.OrderService.csproj", "./"]
RUN dotnet restore "Kinetix.OrderService.csproj"
COPY . .
RUN dotnet publish "Kinetix.OrderService.csproj" -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0-preview AS final
WORKDIR /app
COPY --from=build /app/publish .
ENV PORT=8001
EXPOSE 8001
ENTRYPOINT ["dotnet", "Kinetix.OrderService.dll"]
